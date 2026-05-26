using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Direct-construction unit tests for <see cref="MPassSamlController"/>. The controller
/// is the future Assertion Consumer Service (ACS) endpoint for MEGA's MPass SAML POST
/// binding. For this preparation phase the controller does NOT issue a cookie — it
/// merely parses the assertion and returns a JSON summary so operators can validate
/// connectivity end-to-end before the live middleware swap.
/// </summary>
public sealed class MPassSamlControllerTests
{
    /// <summary>Canonical SAML 2.0 assertion namespace.</summary>
    private static readonly XNamespace Saml = "urn:oasis:names:tc:SAML:2.0:assertion";

    /// <summary>
    /// Stub parser that returns a deterministic outcome configured per-test. Lets the
    /// controller tests exercise the form-decode + Result-to-HTTP mapping without
    /// pulling in the real <see cref="MPassSamlAssertionParser"/>.
    /// </summary>
    private sealed class StubParser(Result<ClaimsPrincipal> outcome) : ISamlAssertionParser
    {
        public Result<ClaimsPrincipal> Parse(string assertionXml, string authenticationScheme, CancellationToken cancellationToken = default)
            => outcome;
    }

    /// <summary>
    /// Builds an <see cref="HttpContext"/> whose form payload contains a single
    /// <c>SAMLResponse</c> field set to the supplied (already-base64-encoded) value.
    /// </summary>
    private static DefaultHttpContext BuildContext(string? samlResponseB64)
    {
        var ctx = new DefaultHttpContext();
        var fields = new Dictionary<string, StringValues>();
        if (samlResponseB64 is not null)
        {
            fields["SAMLResponse"] = samlResponseB64;
        }
        ctx.Request.ContentType = "application/x-www-form-urlencoded";
        ctx.Request.Method = "POST";
        ctx.Request.Form = new FormCollection(fields);
        return ctx;
    }

    /// <summary>
    /// Constructs a minimally valid SAML assertion (only used for the "valid input" path —
    /// the stub parser ignores XML content and returns a canned principal).
    /// </summary>
    private static string SampleAssertionXml()
    {
        var assertion = new XElement(Saml + "Assertion",
            new XAttribute(XNamespace.Xmlns + "saml", Saml.NamespaceName),
            new XElement(Saml + "Issuer", "https://mpass.staging.egov.md"));
        return assertion.ToString();
    }

    [Fact]
    public async Task Acs_ValidSamlPost_Returns200WithParsedStatus()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim("idnp", "2002004123456"),
                new Claim("mpower:principal_idnp", "2009000000001"),
                new Claim("mpower:delegation_id", "DEL-7"),
                new Claim(ClaimTypes.Email, "ion@example.md"),
                new Claim(ClaimTypes.Role, "mpass:cnas/citizen"),
            ],
            "MPassSaml");
        var principal = new ClaimsPrincipal(identity);
        var parser = new StubParser(Result<ClaimsPrincipal>.Success(principal));
        var sut = new MPassSamlController(parser, NullLogger<MPassSamlController>.Instance);
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = BuildContext(Convert.ToBase64String(Encoding.UTF8.GetBytes(SampleAssertionXml()))),
        };

        var result = await sut.AcsAsync(default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(200);
        // The body shape is `{ status, claims }` — assert presence rather than the
        // exact serialisation so future enhancements (extra summary fields) don't
        // break the test unnecessarily.
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        json.Should().Contain("\"status\":\"parsed\"");
        json.Should().NotContain("2002004123456");
        json.Should().NotContain("2009000000001");
        json.Should().NotContain("ion@example.md");
    }

    [Fact]
    public async Task Acs_MalformedSaml_Returns400WithInvalidSamlCode()
    {
        var parser = new StubParser(
            Result<ClaimsPrincipal>.Failure(ErrorCodes.InvalidSaml, "garbage"));
        var sut = new MPassSamlController(parser, NullLogger<MPassSamlController>.Instance);
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = BuildContext(Convert.ToBase64String(Encoding.UTF8.GetBytes("<not></well-formed"))),
        };

        var result = await sut.AcsAsync(default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        System.Text.Json.JsonSerializer.Serialize(problem.Value)
            .Should().Contain(ErrorCodes.InvalidSaml);
    }

    [Fact]
    public async Task Acs_EmptySamlResponseField_Returns400()
    {
        // The stub will never be invoked — the controller must short-circuit before
        // reaching the parser when SAMLResponse is missing or empty.
        var parser = new StubParser(
            Result<ClaimsPrincipal>.Failure(ErrorCodes.InvalidSaml, "should not be reached"));
        var sut = new MPassSamlController(parser, NullLogger<MPassSamlController>.Instance);
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = BuildContext(samlResponseB64: null),
        };

        var result = await sut.AcsAsync(default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Acs_SamlResponseNotBase64_Returns400()
    {
        var parser = new StubParser(
            Result<ClaimsPrincipal>.Failure(ErrorCodes.InvalidSaml, "should not be reached"));
        var sut = new MPassSamlController(parser, NullLogger<MPassSamlController>.Instance);
        sut.ControllerContext = new ControllerContext
        {
            // Force a value that base64 cannot decode (contains illegal chars).
            HttpContext = BuildContext("!!!not-base64!!!"),
        };

        var result = await sut.AcsAsync(default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
        System.Text.Json.JsonSerializer.Serialize(problem.Value)
            .Should().Contain(ErrorCodes.InvalidSaml);
    }

    /// <summary>
    /// In-memory <see cref="ILogger{T}"/> that captures every formatted log entry into a
    /// list so the test can assert the rendered text does NOT contain the raw IDNP fields.
    /// </summary>
    private sealed class CapturingLogger : ILogger<MPassSamlController>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
            // Also capture each structured KVP value as its string form so the test can
            // assert no PII is structure-logged either.
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kv in kvps)
                {
                    if (kv.Value is not null)
                    {
                        Messages.Add(kv.Value.ToString() ?? string.Empty);
                    }
                }
            }
        }
    }

    /// <summary>
    /// iter-149 / Fix 1 — pin the security contract that the SAML controller does
    /// NOT structured-log raw subject / principal IDNPs. The information-class log
    /// after successful parsing must emit only presence booleans plus the
    /// delegationId (a Sqid-shaped identifier, not a national identifier).
    /// </summary>
    [Fact]
    public async Task Acs_ParsedSuccessfully_DoesNotLogRawIdnp()
    {
        const string subjectIdnp = "2002004123456";
        const string principalIdnp = "2009000000001";
        var identity = new ClaimsIdentity(
            [
                new Claim("idnp", subjectIdnp),
                new Claim("mpower:principal_idnp", principalIdnp),
                new Claim("mpower:delegation_id", "DEL-7"),
            ],
            "MPassSaml");
        var principal = new ClaimsPrincipal(identity);
        var parser = new StubParser(Result<ClaimsPrincipal>.Success(principal));
        var capturingLogger = new CapturingLogger();
        var sut = new MPassSamlController(parser, capturingLogger);
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = BuildContext(Convert.ToBase64String(Encoding.UTF8.GetBytes(SampleAssertionXml()))),
        };

        var result = await sut.AcsAsync(default);

        result.Should().BeOfType<OkObjectResult>();
        capturingLogger.Messages.Should().NotBeEmpty();
        // The combined log payload (formatted strings + structured KVP values) must NOT
        // contain either raw IDNP.
        var combined = string.Join("|", capturingLogger.Messages);
        combined.Should().NotContain(subjectIdnp, "the SAML log payload must never contain raw subject IDNPs");
        combined.Should().NotContain(principalIdnp, "the SAML log payload must never contain raw principal IDNPs");
    }
}
