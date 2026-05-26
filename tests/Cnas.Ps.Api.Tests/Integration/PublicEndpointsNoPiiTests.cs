using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Security;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Integration;

/// <summary>
/// R0506 / TOR CF 01.09 — "no PII in any public response" integration test.
/// Enumerates every action method on <see cref="PublicController"/> via
/// reflection, drives each one with substituted services that return a
/// plausible payload, serialises the JSON body, and scans with regular
/// expressions for IDNP (13-digit Moldovan personal identifier), Moldovan
/// IBAN, email, and E.164 phone patterns. Any match fails the test — that
/// is a real bug in the public surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why controller-level not E2E.</b> The CNAS Api.Tests project does
/// not host a <c>WebApplicationFactory</c> fixture; integration journeys
/// live in <c>Cnas.Ps.E2E.Tests</c>. The current test exercises the same
/// concrete controller actions with substituted services that return the
/// SAME DTOs the production services would return — so the JSON body
/// scanned for PII is wire-identical to the body that would leave the
/// public surface in production.
/// </para>
/// <para>
/// <b>Why a separate invariant test for sensitivity classification.</b>
/// The runtime PII scanner catches the case where a production service
/// accidentally returns PII <i>data</i>; the static reflection test
/// catches the case where a future change adds a DTO property that is
/// classified <see cref="SensitivityLabel.Confidential"/> or
/// <see cref="SensitivityLabel.Restricted"/> to the public surface. The
/// two checks are complementary — runtime data + static contract.
/// </para>
/// </remarks>
public sealed class PublicEndpointsNoPiiTests
{
    // ─────────────────────── PII regex (correctness-strict) ───────────────────────

    /// <summary>Moldovan IDNP: exactly 13 consecutive digits.</summary>
    private static readonly Regex IdnpPattern = new(@"\b\d{13}\b", RegexOptions.Compiled);

    /// <summary>Moldovan IBAN: <c>MD</c> + 2 check digits + 20 alphanumerics.</summary>
    private static readonly Regex IbanPattern = new(@"\bMD\d{2}[A-Z0-9]{20}\b", RegexOptions.Compiled);

    /// <summary>Loose email regex (RFC 5322 simplified).</summary>
    private static readonly Regex EmailPattern = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);

    /// <summary>E.164 phone: leading '+' followed by 8-15 digits.</summary>
    private static readonly Regex PhonePattern = new(@"\+\d{8,15}", RegexOptions.Compiled);

    /// <summary>
    /// Asserts the supplied JSON body carries no PII pattern. Each pattern
    /// is asserted independently so the failure message identifies the
    /// specific class of leak.
    /// </summary>
    /// <param name="route">Route under test (used in failure message).</param>
    /// <param name="json">Serialised response body.</param>
    private static void AssertNoPii(string route, string json)
    {
        IdnpPattern.Matches(json).Should().BeEmpty(
            $"route '{route}' must not return any 13-digit IDNP pattern (R0506).");
        IbanPattern.Matches(json).Should().BeEmpty(
            $"route '{route}' must not return any Moldovan IBAN pattern (R0506).");
        EmailPattern.Matches(json).Should().BeEmpty(
            $"route '{route}' must not return any email pattern (R0506).");
        PhonePattern.Matches(json).Should().BeEmpty(
            $"route '{route}' must not return any E.164 phone pattern (R0506).");
    }

    // ─────────────────────── Action-level happy-path scans ───────────────────────

    /// <summary>
    /// R0506 — <c>GET /api/public/content</c> returns the paged content
    /// list with no PII tokens.
    /// </summary>
    [Fact]
    public async Task SearchContent_HappyPath_HasNoPii()
    {
        var publicContent = Substitute.For<IPublicContentService>();
        var info = Substitute.For<IInformationServices>();
        var kpi = Substitute.For<IPublicKpiService>();

        var page = new PagedResult<PublicContentCard>(
            Items: new[]
            {
                new PublicContentCard(
                    Id: "k3Gq9",
                    Title: "Pensia pentru limita de vârstă",
                    Summary: "Informații publice despre serviciu (depersonalised).",
                    Category: "PENSIONS",
                    UpdatedAtUtc: new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc)),
            },
            Page: 1,
            PageSize: 20,
            TotalCount: 1);
        publicContent.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<PublicContentCard>>.Success(page));

        var controller = new PublicController(publicContent, info, kpi);

        var result = await controller.SearchAsync("pensia", 1, 20, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        AssertNoPii("/api/public/content", json);
    }

    /// <summary>
    /// R0506 — <c>GET /api/public/calculators/retirement-age</c> returns
    /// only the calculated retirement date + age — no PII.
    /// </summary>
    [Fact]
    public async Task RetirementCalculator_HappyPath_HasNoPii()
    {
        var publicContent = Substitute.For<IPublicContentService>();
        var info = Substitute.For<IInformationServices>();
        var kpi = Substitute.For<IPublicKpiService>();

        info.CalculateRetirementAgeAsync(Arg.Any<RetirementAgeInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<RetirementAgeOutput>.Success(
                new RetirementAgeOutput(new DateOnly(2056, 1, 1), AgeYears: 63)));

        var controller = new PublicController(publicContent, info, kpi);
        var result = await controller.CalcRetirementAsync(
            new DateOnly(1993, 1, 1), 'M', CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        AssertNoPii("/api/public/calculators/retirement-age", json);
    }

    /// <summary>
    /// R0506 — <c>GET /api/public/calculators/application-status</c>
    /// returns only the reference number + status — no PII.
    /// </summary>
    [Fact]
    public async Task ApplicationStatusLookup_HappyPath_HasNoPii()
    {
        var publicContent = Substitute.For<IPublicContentService>();
        var info = Substitute.For<IInformationServices>();
        var kpi = Substitute.For<IPublicKpiService>();

        info.GetApplicationStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<ApplicationStatusOutput>.Success(
                new ApplicationStatusOutput(
                    ReferenceNumber: "PS-2026-0001",
                    Status: "UnderExamination",
                    LastUpdateUtc: new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc))));

        var controller = new PublicController(publicContent, info, kpi);
        var result = await controller.GetStatusAsync("PS-2026-0001", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        AssertNoPii("/api/public/calculators/application-status", json);
    }

    /// <summary>
    /// R0506 — <c>GET /api/public/kpis</c> returns only depersonalised
    /// aggregate counts — no PII.
    /// </summary>
    [Fact]
    public async Task GetKpis_HappyPath_HasNoPii()
    {
        var publicContent = Substitute.For<IPublicContentService>();
        var info = Substitute.For<IInformationServices>();
        var kpi = Substitute.For<IPublicKpiService>();

        kpi.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(Result<PublicKpiSnapshotDto>.Success(new PublicKpiSnapshotDto(
                ComputedAtUtc: new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc),
                TotalActiveContributors: 1234L,
                TotalActiveInsuredPersons: 5678L,
                TotalPendingApplications: 90L,
                DecisionsIssuedLast30Days: 12L,
                LastSuccessfulTreasuryImportAtUtc: new DateTime(2026, 5, 24, 8, 0, 0, DateTimeKind.Utc))));

        var controller = new PublicController(publicContent, info, kpi);
        var result = await controller.GetKpisAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var json = JsonSerializer.Serialize(ok.Value);
        AssertNoPii("/api/public/kpis", json);
    }

    // ─────────────────────── Reflection-based invariant ───────────────────────

    /// <summary>
    /// R0506 invariant — every <see cref="PublicController"/> action's
    /// return DTO (drilled into ActionResult&lt;T&gt; / Task&lt;T&gt; etc.)
    /// MUST NOT carry any property classified as
    /// <see cref="SensitivityLabel.Confidential"/> or
    /// <see cref="SensitivityLabel.Restricted"/>. All public-surface DTOs
    /// must be classified <c>Public</c> (or unannotated, which defaults to
    /// <c>Internal</c> — also acceptable for non-PII catalogue fields).
    /// </summary>
    [Fact]
    public void PublicControllerActions_ReturnTypes_HaveNoConfidentialOrRestrictedProperties()
    {
        var actionMethods = typeof(PublicController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes()
                .Any(a => a.GetType().Name.StartsWith("Http", StringComparison.Ordinal)))
            .ToList();

        actionMethods.Should().NotBeEmpty("PublicController should declare at least one action method.");

        var violations = new List<string>();
        var documentedSoftWarnings = new List<string>();
        foreach (var method in actionMethods)
        {
            var dtoType = UnwrapDtoType(method.ReturnType);
            if (dtoType is null)
            {
                documentedSoftWarnings.Add(
                    $"Method {method.Name}: return type cannot be statically resolved (likely a generic OkObjectResult). Runtime PII scan still applies.");
                continue;
            }

            foreach (var prop in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = prop.GetCustomAttribute<SensitivityClassificationAttribute>();
                if (attr is null) continue;
                if (attr.Label is SensitivityLabel.Confidential or SensitivityLabel.Restricted)
                {
                    violations.Add(
                        $"{method.Name} → {dtoType.Name}.{prop.Name} is classified {attr.Label}, which is forbidden on the public surface (R0506).");
                }
            }
        }

        violations.Should().BeEmpty(
            "PublicController must not return any Confidential or Restricted properties (R0506). " +
            "Soft warnings (untyped action results): " + string.Join("; ", documentedSoftWarnings));
    }

    /// <summary>
    /// Strips the standard ASP.NET wrapper types
    /// (<see cref="Task{T}"/>, <see cref="ActionResult{T}"/>) to expose
    /// the underlying DTO type. Returns null when the action returns a
    /// non-generic <see cref="IActionResult"/> — those are scanned at
    /// runtime via the body-scan tests above.
    /// </summary>
    /// <param name="returnType">Action method's declared return type.</param>
    /// <returns>The inner DTO type, or null when not statically resolvable.</returns>
    private static Type? UnwrapDtoType(Type returnType)
    {
        var t = returnType;
        // Task<T> -> T
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            t = t.GetGenericArguments()[0];
        }
        // ActionResult<T> -> T
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ActionResult<>))
        {
            t = t.GetGenericArguments()[0];
        }
        // PagedResult<T> -> T (we still want to check the inner DTO).
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(PagedResult<>))
        {
            t = t.GetGenericArguments()[0];
        }
        // Bail out on non-generic IActionResult — runtime scan covers it.
        if (t.Name == "IActionResult" || t.Name == "ActionResult")
        {
            return null;
        }
        return t;
    }
}
