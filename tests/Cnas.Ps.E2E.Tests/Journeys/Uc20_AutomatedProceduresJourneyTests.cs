using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC20 — "Execut proceduri automate" (execute automated procedures). End-to-end journey
/// covering the technical-administrator visibility surface for background job failures.
/// </summary>
/// <remarks>
/// <para>
/// <b>Endpoint surface landed in this batch.</b> The forward control surface for UC20 is
/// now <see cref="Cnas.Ps.Api.Controllers.AutomationController"/> wired over
/// <see cref="Cnas.Ps.Application.UseCases.IAutomationService"/> — operators trigger
/// on-demand runs and update cron schedules through it. The DLQ visibility surface
/// (<see cref="Cnas.Ps.Api.Controllers.AdminController"/>) covers the "what failed?"
/// view from batch #87; this journey exercises BOTH surfaces.
/// </para>
/// <para>
/// <b>Actors.</b>
/// <list type="bullet">
///   <item>cnas-tech-admin — sole authorized persona; the controller's
///         <see cref="Cnas.Ps.Api.Composition.AuthorizationComposition.CnasTechAdmin"/>
///         policy rejects everyone else.</item>
///   <item>cnas-user — used in the forbidden-persona assertion to lock the policy gate.</item>
/// </list>
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 200 OK from <c>GET /api/admin/failed-jobs</c> when called by a
///         <c>cnas-tech-admin</c>.</item>
///   <item>A seeded <see cref="FailedJob"/> row surfaces in the paged result, with the
///         Sqid id round-tripping intact (RULE 3) and the <c>JobName</c> +
///         <c>ExceptionType</c> matching the seed.</item>
///   <item>The <c>jobName</c> query-string filter narrows the result to entries matching
///         the supplied Quartz job key — locks the controller's optional-filter
///         pass-through.</item>
///   <item>HTTP 404 Not Found from <c>POST /api/admin/failed-jobs/{unknown-sqid}/replay</c>
///         when the Sqid is valid but resolves to no row. Locks the
///         <see cref="Cnas.Ps.Core.Common.ErrorCodes.NotFound"/> branch on the store's
///         replay surface.</item>
///   <item>HTTP 403 Forbidden when a <c>cnas-user</c> calls <c>GET /api/admin/failed-jobs</c>
///         — defense in depth for the controller-level policy.</item>
/// </list>
/// </para>
/// <para>
/// <b>What is NOT asserted here.</b> The successful-replay path is intentionally NOT
/// exercised because it depends on a registered Quartz job key; the E2E host's
/// scheduler does not register the <c>uc20-e2e-probe</c> job, so a successful replay
/// would 400 with a "job is not registered" error. The 404 branch covers the controller's
/// failure-mapping surface without that dependency.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc20_AutomatedProceduresJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc20_AutomatedProceduresJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A technical administrator queries the DLQ, sees a seeded failure row, narrows by
    /// job name, and the unknown-Sqid replay path returns 404.
    /// </summary>
    [Fact]
    public async Task ListAndReplay_TechAdminPersona_ReturnsSeededRowAnd404OnUnknownSqid()
    {
        // Arrange — seed a FailedJob row directly so the listing has a deterministic
        // entry to assert against. Using a unique JobName scoped to this test so other
        // journeys cannot contaminate the assertion (the DI graph is shared by xUnit
        // collection scope).
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var seedFailedAt = DateTime.UtcNow.AddMinutes(-3);
        var seed = new FailedJob
        {
            JobName = "uc20-e2e-probe",
            JobGroup = "DEFAULT",
            FailedAtUtc = seedFailedAt,
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "UC20 E2E seeded failure — synthetic.",
            StackTrace = "   at Cnas.Ps.E2E.Tests.Uc20Seed.Throw()",
            JobDataJson = "{}",
            RefireCount = 0,
            CreatedAtUtc = seedFailedAt,
            IsActive = true,
        };
        db.FailedJobs.Add(seed);
        await db.SaveChangesAsync();

        var seedSqid = sqids.Encode(seed.Id);
        var adminSqid = sqids.Encode(200_001);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: adminSqid, Roles: ["cnas-tech-admin"])));

        // Act 1 — list the DLQ filtered by the seeded job name so the assertion is not
        // polluted by any rows another test inadvertently leaves behind.
        using var listResponse = await client.GetAsync(
            "/api/admin/failed-jobs?jobName=uc20-e2e-probe&page=1&pageSize=20");

        // Assert — 200 with the seeded row present.
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await listResponse.Content.ReadAsStringAsync());
        var page = await listResponse.Content
            .ReadFromJsonAsync<PagedResult<FailedJobOutput>>();
        page.Should().NotBeNull();
        page!.Items.Should().Contain(item => item.Id == seedSqid,
            "the seeded FailedJob must surface in the filtered DLQ listing");
        var match = page.Items.Single(item => item.Id == seedSqid);
        match.JobName.Should().Be("uc20-e2e-probe");
        match.ExceptionType.Should().Be("System.InvalidOperationException");
        match.ReplayState.Should().BeNull(
            "a freshly-seeded row has never been replayed — ReplayState must be null");

        // Act 2 — replay against a Sqid that does not resolve to any row. Encoding 999_999
        // far exceeds any seeded primary key so the store's NotFound branch fires.
        var unknownSqid = sqids.Encode(999_999_999L);
        using var replayResponse = await client.PostAsync(
            $"/api/admin/failed-jobs/{unknownSqid}/replay",
            content: null);

        // Assert — 404 NotFound; the controller maps ErrorCodes.NotFound → NotFound().
        replayResponse.StatusCode.Should().Be(HttpStatusCode.NotFound,
            await replayResponse.Content.ReadAsStringAsync());

        // Assert — the seeded row was NOT replayed (LastReplayAtUtc remains null) since
        // we only touched the unknown-Sqid branch. Re-read through a fresh scope so we
        // observe the post-request state of the entity.
        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var refreshed = await verifyDb.FailedJobs.AsNoTracking()
            .SingleOrDefaultAsync(f => f.Id == seed.Id);
        refreshed.Should().NotBeNull();
        refreshed!.LastReplayAtUtc.Should().BeNull(
            "the unknown-Sqid replay must not mutate any existing DLQ row");
        refreshed.ReplayState.Should().BeNull();
    }

    /// <summary>
    /// A technical administrator triggers an on-demand automation run via
    /// <c>POST /api/automation/{code}/run-now</c>. The current service implementation is a
    /// fire-and-forget stub that returns Success(); the controller maps that to 202 Accepted
    /// per the AutomationController contract.
    /// </summary>
    [Fact]
    public async Task RunNow_TechAdminPersona_Returns202Accepted()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(200_010), Roles: ["cnas-tech-admin"])));

        // Act — no body; controller forwards "{}" as the parameters JSON.
        using var response = await client.PostAsync(
            "/api/automation/uc20-probe/run-now", content: null);

        // Assert — the service stub returns Success() → controller returns 202 Accepted.
        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A <c>cnas-user</c> persona is rejected by the AutomationController's CnasTechAdmin
    /// policy gate when attempting to run an automation — defense in depth for the policy.
    /// </summary>
    [Fact]
    public async Task RunNow_UserPersona_Returns403()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(200_011), Roles: ["cnas-user"])));

        // Act
        using var response = await client.PostAsync(
            "/api/automation/uc20-probe/run-now", content: null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A <c>cnas-user</c> persona (read-only staff) is rejected by the controller's policy
    /// gate when attempting to list the DLQ — only the <c>cnas-tech-admin</c> persona can
    /// inspect background-job failures.
    /// </summary>
    [Fact]
    public async Task ListFailedJobs_UserPersonaWithoutTechAdminRole_Returns403()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(200_002), Roles: ["cnas-user"])));

        using var response = await client.GetAsync("/api/admin/failed-jobs");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
    }
}
