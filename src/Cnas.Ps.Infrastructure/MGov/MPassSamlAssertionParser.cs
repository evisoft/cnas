using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.MGov;

/// <summary>
/// <see cref="ISamlAssertionParser"/> implementation backed by hand-rolled
/// <see cref="XDocument"/> parsing. Mirrors the parsing style used by
/// <c>MSignClient</c> so the codebase keeps a single XML-handling idiom and so the
/// adapter has no dependency on a SAML library (the deferred middleware swap will
/// bring one in).
/// </summary>
/// <remarks>
/// <para>
/// The parser enforces three structural / temporal invariants:
/// </para>
/// <list type="number">
///   <item>The document root is <c>&lt;saml:Assertion&gt;</c> with a <c>&lt;Conditions&gt;</c> child.</item>
///   <item>The current UTC instant (from <see cref="ICnasTimeProvider"/>) is within <c>[NotBefore - ClockSkew, NotOnOrAfter + ClockSkew]</c>.</item>
///   <item>The configured SP entity id appears in <c>&lt;Conditions&gt;/&lt;AudienceRestriction&gt;/&lt;Audience&gt;</c>.</item>
/// </list>
/// <para>
/// Each SAML <c>&lt;Attribute Name="..."&gt;</c> found in <c>&lt;AttributeStatement&gt;</c>
/// is mapped through <see cref="MPassSamlOptions.AttributeMap"/>; unknown names are
/// logged at Debug and dropped. Multi-valued attributes emit one claim per value.
/// </para>
/// <para>
/// <b>The XMLDSig signature element is NOT validated by this implementation.</b>
/// </para>
/// </remarks>
/// <param name="options">Bound MPass SAML options snapshot.</param>
/// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="logger">Structured logger; never receives raw assertion content.</param>
public sealed class MPassSamlAssertionParser(
    IOptions<MPassSamlOptions> options,
    ICnasTimeProvider clock,
    ILogger<MPassSamlAssertionParser> logger) : ISamlAssertionParser
{
    private readonly MPassSamlOptions _options = options.Value;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<MPassSamlAssertionParser> _logger = logger;

    /// <summary>Canonical SAML 2.0 assertion namespace.</summary>
    private const string SamlNs = "urn:oasis:names:tc:SAML:2.0:assertion";

    /// <inheritdoc />
    // TODO[mpass-saml]: validate the XMLDSig signature once the MEGA staging cert is
    // provisioned via semnatura.md. Until then the parser accepts any structurally-valid
    // assertion within the configured audience + validity window. The current preparation
    // phase is explicitly NOT a security boundary — the live middleware swap (see
    // docs/EGOV-INTEGRATION-GAP.md §MPass) will introduce signature validation.
    public Result<ClaimsPrincipal> Parse(
        string assertionXml,
        string authenticationScheme,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertionXml);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);

        // 1. Parse the XML envelope. Any well-formedness failure (truncated payload,
        //    illegal entities, mismatched tags) maps to INVALID_SAML rather than
        //    bubbling up XmlException to the caller.
        XDocument doc;
        try
        {
            doc = XDocument.Parse(assertionXml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "MPass SAML assertion XML is malformed.");
            return Result<ClaimsPrincipal>.Failure(
                ErrorCodes.InvalidSaml, "SAML assertion XML is malformed.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Locate the <Assertion> element. We tolerate either an inline assertion or
        //    one wrapped inside a <Response> envelope, scanning by local-name so namespace
        //    prefix variations don't trip us up.
        var assertionEl = doc.Root?.Name.LocalName == "Assertion"
            ? doc.Root
            : doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Assertion");
        if (assertionEl is null)
        {
            return Result<ClaimsPrincipal>.Failure(
                ErrorCodes.InvalidSaml, "SAML assertion root element is missing.");
        }

        // 3. Conditions — required. Absence means we cannot enforce the validity window,
        //    which we treat as a structural defect rather than "no expiry".
        if (!_options.AllowUnsignedAssertionsForTesting)
        {
            return Result<ClaimsPrincipal>.Failure(
                ErrorCodes.InvalidSaml,
                "SAML assertion signature validation is required before claims are accepted.");
        }

        var conditionsEl = assertionEl
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Conditions");
        if (conditionsEl is null)
        {
            return Result<ClaimsPrincipal>.Failure(
                ErrorCodes.InvalidSaml, "SAML assertion is missing <Conditions>.");
        }

        var now = _clock.UtcNow;
        var skew = _options.ClockSkew;

        // 3a. NotBefore — assertion is not yet valid (after subtracting clock-skew tolerance).
        //     Fail-CLOSED on parse failure: a malformed timestamp must NOT bypass the
        //     validity-window check. Splitting the AND chain (instead of the prior
        //     short-circuit `TryParse && ...`) is what closes the temporal fail-open.
        var notBeforeRaw = conditionsEl.Attribute("NotBefore")?.Value;
        if (!string.IsNullOrWhiteSpace(notBeforeRaw))
        {
            if (!TryParseInstant(notBeforeRaw, out var notBefore))
            {
                return Result<ClaimsPrincipal>.Failure(
                    ErrorCodes.InvalidSaml,
                    "SAML assertion has an unparseable NotBefore attribute.");
            }
            if (now + skew < notBefore)
            {
                return Result<ClaimsPrincipal>.Failure(
                    ErrorCodes.SamlAssertionExpired,
                    "SAML assertion is not yet valid (NotBefore is in the future).");
            }
        }

        // 3b. NotOnOrAfter — assertion has expired (after adding clock-skew tolerance).
        //     Same fail-closed reasoning as NotBefore above.
        var notAfterRaw = conditionsEl.Attribute("NotOnOrAfter")?.Value;
        if (!string.IsNullOrWhiteSpace(notAfterRaw))
        {
            if (!TryParseInstant(notAfterRaw, out var notAfter))
            {
                return Result<ClaimsPrincipal>.Failure(
                    ErrorCodes.InvalidSaml,
                    "SAML assertion has an unparseable NotOnOrAfter attribute.");
            }
            if (now - skew >= notAfter)
            {
                return Result<ClaimsPrincipal>.Failure(
                    ErrorCodes.SamlAssertionExpired,
                    "SAML assertion has expired (NotOnOrAfter is in the past).");
            }
        }

        // 4. AudienceRestriction — required, MUST contain our configured SP entity id.
        //    A missing audience restriction is treated as a mismatch (not a structural
        //    INVALID_SAML) because the assertion is well-formed; it's just not for us.
        var audienceMatched = false;
        foreach (var restriction in conditionsEl.Elements()
            .Where(e => e.Name.LocalName == "AudienceRestriction"))
        {
            foreach (var audienceEl in restriction.Elements()
                .Where(e => e.Name.LocalName == "Audience"))
            {
                if (string.Equals(audienceEl.Value, _options.ServiceProviderEntityId, StringComparison.Ordinal))
                {
                    audienceMatched = true;
                    break;
                }
            }
            if (audienceMatched) break;
        }
        if (!audienceMatched)
        {
            return Result<ClaimsPrincipal>.Failure(
                ErrorCodes.SamlAssertionAudienceMismatch,
                "SAML assertion does not list the configured CNAS service-provider audience.");
        }

        // 5. AttributeStatement — collect every <Attribute> element and emit claims
        //    according to the configured map. Unknown attribute names are logged at
        //    Debug and skipped silently per the spec.
        var claims = new List<Claim>();
        foreach (var stmt in assertionEl.Elements()
            .Where(e => e.Name.LocalName == "AttributeStatement"))
        {
            foreach (var attr in stmt.Elements().Where(e => e.Name.LocalName == "Attribute"))
            {
                var name = attr.Attribute("Name")?.Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                if (!_options.AttributeMap.TryGetValue(name, out var claimType))
                {
                    _logger.LogDebug("MPass SAML attribute {AttributeName} is not in the configured map — ignored.", name);
                    continue;
                }
                foreach (var value in attr.Elements().Where(e => e.Name.LocalName == "AttributeValue"))
                {
                    var raw = value.Value;
                    if (!string.IsNullOrEmpty(raw))
                    {
                        claims.Add(new Claim(claimType, raw));
                    }
                }
            }
        }

        // The scheme name lives on ClaimsIdentity.AuthenticationType — every downstream
        // authorization check inspects it to decide whether the identity is authenticated.
        var identity = new ClaimsIdentity(claims, authenticationScheme);
        return Result<ClaimsPrincipal>.Success(new ClaimsPrincipal(identity));
    }

    /// <summary>
    /// Parses an ISO-8601 (XSD <c>dateTime</c>) instant produced by MPass into a UTC
    /// <see cref="DateTime"/>. Returns <c>false</c> when the value is not parseable so
    /// the caller can treat it as "absent" rather than throwing.
    /// </summary>
    /// <param name="raw">Raw attribute value (e.g. <c>2026-05-20T08:00:00Z</c>).</param>
    /// <param name="instant">Resulting UTC instant, or <see cref="DateTime.MinValue"/> on failure.</param>
    /// <returns><c>true</c> when the value was parsed; otherwise <c>false</c>.</returns>
    private static bool TryParseInstant(string raw, out DateTime instant)
    {
        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            instant = parsed;
            return true;
        }
        instant = DateTime.MinValue;
        return false;
    }

    /// <summary>
    /// Suppresses an unused-field warning for the dictionary type used by callers
    /// extending the parser via configuration. Reserved for future composition where
    /// extra static maps may live next to the runtime instance.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Style",
        "IDE0051:Remove unused private members",
        Justification = "Reserved for future extension; intentionally kept for symmetry with MSignClient.")]
    private static readonly IReadOnlyDictionary<string, string> _emptyMap = new Dictionary<string, string>();
}
