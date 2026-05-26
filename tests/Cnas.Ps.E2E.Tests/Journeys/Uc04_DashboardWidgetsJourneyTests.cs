using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC04 — "Vizualizare dashboard". End-to-end journey covering the dashboard widget
/// surface (<c>GET /api/dashboard/widgets</c>). Combines authenticated access, EF Core
/// aggregation, and the sliding-window <c>Authenticated</c> rate-limit partition into a
/// single happy-path request that any future regression in those layers will break.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> CNAS staff — authenticated via <see cref="TestAuthHandler"/> with the
/// <c>cnas-user</c> role. The dashboard policy only requires an authenticated principal,
/// so any role suffices.
/// </para>
/// <para>
/// <b>Business outcome asserted.</b>
/// <list type="number">
///   <item>HTTP 200 OK from <c>GET /api/dashboard/widgets</c>.</item>
///   <item>The response is a non-empty JSON array of <see cref="KpiWidget"/> entries.</item>
///   <item>The <c>APPS_OPEN</c> widget reflects seeded application data — the counter
///         increases by the number of seeded "open" rows we inserted before the call.
///         Locks down the wiring between the controller, the
///         <c>DashboardService.GetWidgetsAsync</c> aggregator, and the EF Core InMemory
///         provider.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc04_DashboardWidgetsJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc04_DashboardWidgetsJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Seeded open applications surface in the <c>APPS_OPEN</c> KPI widget, confirming
    /// the dashboard aggregator reads from the same DbContext that test setup writes to.
    /// </summary>
    [Fact]
    public async Task Widgets_AuthenticatedCaller_ReturnsKpiArrayReflectingSeededData()
    {
        // Arrange — seed a service passport + N open applications. The dashboard counts
        // applications whose Status is not Closed/Rejected so Submitted rows are included.
        const int seededOpenApps = 3;
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        var beforeOpenCount = db.Applications
            .Count(a => a.IsActive
                && a.Status != ApplicationStatus.Closed
                && a.Status != ApplicationStatus.Rejected);

        var passport = new ServicePassport
        {
            Code = "SP-UC04-E2E",
            NameRo = "Serviciu E2E UC04",
            DescriptionRo = "Pașaport seed pentru jurnalul UC04.",
            WorkflowCode = "wf-e2e",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        const string idnp = "2900000000048";
        var solicitant = new Solicitant
        {
            NationalId = idnp,
            NationalIdHash = hasher.ComputeHash(idnp),
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "UC04 Solicitant",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);
        await db.SaveChangesAsync();

        for (int i = 0; i < seededOpenApps; i++)
        {
            db.Applications.Add(new ServiceApplication
            {
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Submitted,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                SubmittedAtUtc = DateTime.UtcNow,
                ReferenceNumber = $"UC04-{i:D2}-{Guid.NewGuid():N}".Substring(0, 32),
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();

        var callerSqid = sqids.Encode(400_001);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: callerSqid, Roles: ["cnas-user"])));

        // Act
        using var response = await client.GetAsync("/api/dashboard/widgets");

        // Assert — 200 with at least the three default widget codes and a counter that
        // reflects the seeded rows.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var widgets = await response.Content.ReadFromJsonAsync<IReadOnlyList<KpiWidget>>();
        widgets.Should().NotBeNull();
        widgets!.Should().NotBeEmpty("the dashboard contract returns at least the default widget set");
        var appsOpen = widgets!.SingleOrDefault(w => w.Code == "APPS_OPEN");
        appsOpen.Should().NotBeNull("APPS_OPEN is part of the default widget set");
        appsOpen!.Value.Should().BeGreaterThanOrEqualTo(beforeOpenCount + seededOpenApps,
            "the open-applications counter must include every row seeded by this test");
    }

    /// <summary>
    /// R0530 / CF 04.01 — a plain <c>cnas-user</c> MUST NOT see the
    /// <c>APPROVAL_QUEUE</c> tile (it is gated to decider / admin / supervisor
    /// roles in the registry). Pins the deny-by-default contract end-to-end.
    /// </summary>
    [Fact]
    public async Task Widgets_PlainCnasUser_DoesNotSeeApprovalQueueWidget()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var callerSqid = sqids.Encode(400_011);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: callerSqid, Roles: ["cnas-user"])));

        using var response = await client.GetAsync("/api/dashboard/widgets");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var widgets = await response.Content.ReadFromJsonAsync<IReadOnlyList<KpiWidget>>();
        widgets.Should().NotBeNull();
        widgets!.Should().NotContain(w => w.Code == "APPROVAL_QUEUE",
            "APPROVAL_QUEUE is gated to decider/admin roles per CF 04.01");
    }

    /// <summary>
    /// R0531 / CF 04.02 — every widget the server returns MUST carry one of the
    /// five canonical category names. Pins the five-bucket split end-to-end.
    /// </summary>
    [Fact]
    public async Task Widgets_EveryReturnedWidget_CarriesCanonicalCategory()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var callerSqid = sqids.Encode(400_021);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: callerSqid, Roles: ["cnas-admin"])));

        using var response = await client.GetAsync("/api/dashboard/widgets");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var widgets = await response.Content.ReadFromJsonAsync<IReadOnlyList<KpiWidget>>();
        widgets.Should().NotBeNull();
        widgets!.Should().NotBeEmpty();

        var canonicalNames = Enum.GetNames<DashboardCategory>();
        widgets!.Should().OnlyContain(
            w => w.Category != null && canonicalNames.Contains(w.Category),
            "every emitted widget MUST be tagged with one of the five CF 04.02 categories");
    }
}
