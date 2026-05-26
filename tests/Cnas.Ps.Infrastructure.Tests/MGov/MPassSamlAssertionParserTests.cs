using System;
using System.Linq;
using System.Security.Claims;
using System.Xml.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Unit tests for <see cref="MPassSamlAssertionParser"/>. The parser is the foundation
/// for the future SAML 2.0 sign-in flow (see <c>docs/EGOV-INTEGRATION-GAP.md</c> §MPass).
/// Each test constructs a real SAML assertion XML using <see cref="XDocument"/> so the
/// parser is exercised end-to-end with the exact wire shape MEGA emits.
/// </summary>
public class MPassSamlAssertionParserTests
{
    /// <summary>Canonical SAML 2.0 assertion namespace.</summary>
    private static readonly XNamespace Saml = "urn:oasis:names:tc:SAML:2.0:assertion";

    /// <summary>Stable SP entity id used by the unit tests.</summary>
    private const string SpEntityId = "https://cnas.gov.md/sp";

    /// <summary>Stable authentication scheme name passed into the parser by the tests.</summary>
    private const string SchemeName = "MPassSaml";

    /// <summary>
    /// Builds a parser wired to a deterministic clock and a fixed SP entity id. Tests
    /// may override the clock (to exercise expiry edges) or the options bag.
    /// </summary>
    private static (MPassSamlAssertionParser Parser, TestClock Clock) BuildSut(
        MPassSamlOptions? options = null,
        DateTime? now = null)
    {
        var clock = new TestClock();
        if (now.HasValue)
        {
            clock.UtcNow = now.Value;
        }
        var opts = Options.Create(options ?? new MPassSamlOptions
        {
            IssuerUrl = "https://mpass.staging.egov.md",
            ServiceProviderEntityId = SpEntityId,
            AllowUnsignedAssertionsForTesting = true,
        });
        var sut = new MPassSamlAssertionParser(opts, clock, NullLogger<MPassSamlAssertionParser>.Instance);
        return (sut, clock);
    }

    /// <summary>
    /// Constructs a minimal-but-valid <c>&lt;saml:Assertion&gt;</c> XML document. Tests
    /// supply attribute values via the <paramref name="attributes"/> parameter and may
    /// override the validity window or the audience.
    /// </summary>
    private static string BuildAssertion(
        DateTime notBefore,
        DateTime notOnOrAfter,
        string audience = SpEntityId,
        (string Name, string[] Values)[]? attributes = null,
        bool includeAudience = true)
    {
        var conditions = new XElement(Saml + "Conditions",
            new XAttribute("NotBefore", notBefore.ToString("o")),
            new XAttribute("NotOnOrAfter", notOnOrAfter.ToString("o")));
        if (includeAudience)
        {
            conditions.Add(new XElement(Saml + "AudienceRestriction",
                new XElement(Saml + "Audience", audience)));
        }

        var stmt = new XElement(Saml + "AttributeStatement");
        if (attributes is not null)
        {
            foreach (var (name, values) in attributes)
            {
                var attr = new XElement(Saml + "Attribute", new XAttribute("Name", name));
                foreach (var v in values)
                {
                    attr.Add(new XElement(Saml + "AttributeValue", v));
                }
                stmt.Add(attr);
            }
        }

        var assertion = new XElement(Saml + "Assertion",
            new XAttribute(XNamespace.Xmlns + "saml", Saml.NamespaceName),
            new XAttribute("Version", "2.0"),
            new XAttribute("ID", "_assertion-1"),
            new XAttribute("IssueInstant", notBefore.ToString("o")),
            new XElement(Saml + "Issuer", "https://mpass.staging.egov.md"),
            new XElement(Saml + "Subject",
                new XElement(Saml + "NameID", "2002004123456")),
            conditions,
            stmt);

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), assertion).ToString();
    }

    [Fact]
    public void Parse_ValidAssertion_ReturnsClaimsPrincipalWithIdnpAndName()
    {
        var (sut, clock) = BuildSut();
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(-1),
            notOnOrAfter: clock.UtcNow.AddMinutes(10),
            attributes:
            [
                ("urn:egov.md/mpass/idnp", ["2002004123456"]),
                ("urn:egov.md/mpass/full_name", ["Ion Popescu"]),
                ("urn:egov.md/mpass/email", ["ion@example.md"]),
            ]);

        var result = sut.Parse(xml, SchemeName);

        result.IsSuccess.Should().BeTrue();
        result.Value.FindFirst("idnp")?.Value.Should().Be("2002004123456");
        result.Value.FindFirst(ClaimTypes.Name)?.Value.Should().Be("Ion Popescu");
        result.Value.FindFirst(ClaimTypes.Email)?.Value.Should().Be("ion@example.md");
        // Authentication scheme is propagated to the identity so middleware can attribute claims.
        result.Value.Identity!.AuthenticationType.Should().Be(SchemeName);
    }

    [Fact]
    public void Parse_UnsignedAssertionWithSecureDefaults_ReturnsInvalidSaml()
    {
        var clock = new TestClock();
        var options = new MPassSamlOptions
        {
            IssuerUrl = "https://mpass.staging.egov.md",
            ServiceProviderEntityId = SpEntityId,
        };
        var sut = new MPassSamlAssertionParser(
            Options.Create(options),
            clock,
            NullLogger<MPassSamlAssertionParser>.Instance);
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(-1),
            notOnOrAfter: clock.UtcNow.AddMinutes(10),
            attributes:
            [
                ("urn:egov.md/mpass/idnp", ["2002004123456"]),
            ]);

        var result = sut.Parse(xml, SchemeName);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSaml);
    }

    [Fact]
    public void Parse_MultiValuedRoleAttribute_EmitsOneClaimPerValue()
    {
        var (sut, clock) = BuildSut();
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(-1),
            notOnOrAfter: clock.UtcNow.AddMinutes(10),
            attributes:
            [
                ("urn:egov.md/mpass/role", ["mpass:cnas/citizen", "mpass:cnas/examiner"]),
            ]);

        var result = sut.Parse(xml, SchemeName);

        result.IsSuccess.Should().BeTrue();
        var roles = result.Value.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        roles.Should().BeEquivalentTo("mpass:cnas/citizen", "mpass:cnas/examiner");
    }

    [Fact]
    public void Parse_DelegationAttributes_MapToMPowerClaims()
    {
        var (sut, clock) = BuildSut();
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(-1),
            notOnOrAfter: clock.UtcNow.AddMinutes(10),
            attributes:
            [
                ("urn:egov.md/mpower/principal_idnp", ["2002004123456"]),
                ("urn:egov.md/mpower/delegation_id", ["DEL-9999"]),
            ]);

        var result = sut.Parse(xml, SchemeName);

        result.IsSuccess.Should().BeTrue();
        result.Value.FindFirst("mpower:principal_idnp")?.Value.Should().Be("2002004123456");
        result.Value.FindFirst("mpower:delegation_id")?.Value.Should().Be("DEL-9999");
    }

    [Fact]
    public void Parse_ExpiredNotOnOrAfter_ReturnsSamlAssertionExpired()
    {
        var (sut, clock) = BuildSut();
        // NotOnOrAfter sits well in the past (outside the 5-minute clock-skew window).
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(-30),
            notOnOrAfter: clock.UtcNow.AddMinutes(-10));

        var result = sut.Parse(xml, SchemeName);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.SamlAssertionExpired);
    }

    [Fact]
    public void Parse_NotBeforeInFuture_ReturnsSamlAssertionExpired()
    {
        var (sut, clock) = BuildSut();
        // NotBefore sits well in the future (outside the 5-minute clock-skew window).
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(10),
            notOnOrAfter: clock.UtcNow.AddMinutes(20));

        var result = sut.Parse(xml, SchemeName);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.SamlAssertionExpired);
    }

    [Fact]
    public void Parse_WithinClockSkew_Succeeds()
    {
        var (sut, clock) = BuildSut();
        // NotBefore is 2 minutes in the future, but the 5-minute clock-skew tolerance
        // accepts it. NotOnOrAfter is well in the future so the post-window check passes.
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(2),
            notOnOrAfter: clock.UtcNow.AddMinutes(20),
            attributes:
            [
                ("urn:egov.md/mpass/idnp", ["2002004123456"]),
            ]);

        var result = sut.Parse(xml, SchemeName);

        result.IsSuccess.Should().BeTrue();
        result.Value.FindFirst("idnp")?.Value.Should().Be("2002004123456");
    }

    [Fact]
    public void Parse_AudienceMismatch_ReturnsSamlAssertionAudienceMismatch()
    {
        var (sut, clock) = BuildSut();
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(-1),
            notOnOrAfter: clock.UtcNow.AddMinutes(10),
            audience: "https://someone-else.example.md/sp");

        var result = sut.Parse(xml, SchemeName);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.SamlAssertionAudienceMismatch);
    }

    [Fact]
    public void Parse_MalformedXml_ReturnsInvalidSaml()
    {
        var (sut, _) = BuildSut();

        var result = sut.Parse("<not><well-formed", SchemeName);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSaml);
    }

    [Fact]
    public void Parse_UnknownAttribute_IsIgnoredSilently()
    {
        var (sut, clock) = BuildSut();
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(-1),
            notOnOrAfter: clock.UtcNow.AddMinutes(10),
            attributes:
            [
                ("urn:egov.md/mpass/idnp", ["2002004123456"]),
                ("urn:egov.md/unknown/quirk", ["should-not-surface"]),
            ]);

        var result = sut.Parse(xml, SchemeName);

        result.IsSuccess.Should().BeTrue();
        // Known attribute survives.
        result.Value.FindFirst("idnp")?.Value.Should().Be("2002004123456");
        // Unknown attribute is dropped — no claim under its raw name, no claim under the value.
        result.Value.FindAll(c => c.Value == "should-not-surface").Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoAudienceRestriction_ReturnsSamlAssertionAudienceMismatch()
    {
        var (sut, clock) = BuildSut();
        var xml = BuildAssertion(
            notBefore: clock.UtcNow.AddMinutes(-1),
            notOnOrAfter: clock.UtcNow.AddMinutes(10),
            includeAudience: false);

        var result = sut.Parse(xml, SchemeName);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.SamlAssertionAudienceMismatch);
    }

    /// <summary>
    /// Temporal fail-closed: a malformed <c>NotBefore</c> attribute (e.g. truncated /
    /// non-ISO) must NOT silently bypass the validity-window check. The parser must
    /// reject it as <see cref="ErrorCodes.InvalidSaml"/> instead of treating an
    /// unparseable timestamp as "no constraint".
    /// </summary>
    [Fact]
    public void Parse_CorruptNotBefore_ReturnsInvalidSaml()
    {
        var (sut, clock) = BuildSut();
        // Hand-craft an assertion with a clearly garbage NotBefore attribute. We bypass
        // BuildAssertion since it formats NotBefore via DateTime.ToString("o").
        var conditions = new XElement(Saml + "Conditions",
            new XAttribute("NotBefore", "not-a-date"),
            new XAttribute("NotOnOrAfter", clock.UtcNow.AddMinutes(10).ToString("o")),
            new XElement(Saml + "AudienceRestriction",
                new XElement(Saml + "Audience", SpEntityId)));
        var assertion = new XElement(Saml + "Assertion",
            new XAttribute(XNamespace.Xmlns + "saml", Saml.NamespaceName),
            new XAttribute("Version", "2.0"),
            new XAttribute("ID", "_corrupt-notbefore"),
            new XAttribute("IssueInstant", clock.UtcNow.ToString("o")),
            new XElement(Saml + "Issuer", "https://mpass.staging.egov.md"),
            conditions);
        var xml = new XDocument(assertion).ToString();

        var result = sut.Parse(xml, SchemeName);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSaml);
    }

    /// <summary>
    /// Same fail-closed guarantee as <see cref="Parse_CorruptNotBefore_ReturnsInvalidSaml"/>
    /// but for the <c>NotOnOrAfter</c> attribute — an unparseable expiry must be rejected,
    /// not silently treated as "no expiry".
    /// </summary>
    [Fact]
    public void Parse_CorruptNotOnOrAfter_ReturnsInvalidSaml()
    {
        var (sut, clock) = BuildSut();
        var conditions = new XElement(Saml + "Conditions",
            new XAttribute("NotBefore", clock.UtcNow.AddMinutes(-10).ToString("o")),
            new XAttribute("NotOnOrAfter", "yesterday-ish"),
            new XElement(Saml + "AudienceRestriction",
                new XElement(Saml + "Audience", SpEntityId)));
        var assertion = new XElement(Saml + "Assertion",
            new XAttribute(XNamespace.Xmlns + "saml", Saml.NamespaceName),
            new XAttribute("Version", "2.0"),
            new XAttribute("ID", "_corrupt-notonorafter"),
            new XAttribute("IssueInstant", clock.UtcNow.ToString("o")),
            new XElement(Saml + "Issuer", "https://mpass.staging.egov.md"),
            conditions);
        var xml = new XDocument(assertion).ToString();

        var result = sut.Parse(xml, SchemeName);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSaml);
    }

    [Fact]
    public void Parse_MissingConditions_ReturnsInvalidSaml()
    {
        var (sut, _) = BuildSut();
        // Hand-craft an assertion without <Conditions> — the parser must reject it as
        // structurally invalid rather than treating it as "no expiry".
        var assertion = new XElement(Saml + "Assertion",
            new XAttribute(XNamespace.Xmlns + "saml", Saml.NamespaceName),
            new XAttribute("Version", "2.0"),
            new XAttribute("ID", "_no-conditions"),
            new XAttribute("IssueInstant", DateTime.UtcNow.ToString("o")),
            new XElement(Saml + "Issuer", "https://mpass.staging.egov.md"));
        var xml = new XDocument(assertion).ToString();

        var result = sut.Parse(xml, SchemeName);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSaml);
    }
}
