using System.Net;
using System.Net.Http;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC23 — "Jurnalizez evenimente" (Annex 1 registry branch). Companion journey to
/// <c>Uc23_AuditLogJourneyTests</c> covering a Critical mutation on the
/// <c>Plătitori de contribuții</c> registry. Flipping the insolvency flag is one of the
/// CNAS-critical events the audit pipeline must capture per SEC 042; this journey locks
/// the wiring between <c>ContributorsController.MarkInsolventAsync</c>, the service-layer
/// mutation, the <see cref="AuditLog"/> writer, and the persisted row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> CNAS operator — authenticated via <see cref="TestAuthHandler"/> with
/// the <c>cnas-user</c> role. The contributors controller accepts either <c>cnas-user</c>
/// or <c>cnas-admin</c>; the lower-privilege role is sufficient for this mutation.
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 204 No Content from <c>POST /api/contributors/{id}/insolvent</c>.</item>
///   <item>The <see cref="Contributor.IsInsolvent"/> flag flips to <c>true</c>.</item>
///   <item>An <see cref="AuditLog"/> row with
///         <c>EventCode = "CONTRIBUTOR.INSOLVENT_SET"</c> exists with
///         <see cref="AuditSeverity.Critical"/> severity, targeting the contributor by
///         id and naming the operator Sqid as <c>ActorId</c>.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc23b_ContributorInsolvencyAuditJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc23b_ContributorInsolvencyAuditJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A CNAS operator flags a contributor as insolvent and the mutation is journaled.
    /// </summary>
    [Fact]
    public async Task MarkInsolvent_OperatorPersona_FlipsFlagAndAudits()
    {
        // Arrange — seed an existing, solvent contributor. The journey mutates an
        // already-active row so the "register + flip" path is decoupled from this test
        // (Uc12a covers registration end-to-end).
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        // A valid IDNO unrelated to UC12a's seed so the two journeys can run in any order.
        const string idno = "1003600012355";
        var contributor = new Contributor
        {
            Idno = idno,
            IdnoHash = hasher.ComputeHash(idno),
            Denumire = "UC23b Insolvency E2E SRL",
            CfojCode = "1170",
            CaemCode = "47111",
            IsInsolvent = false,
            RegisteredAtUtc = DateTime.UtcNow.AddDays(-30),
            CreatedAtUtc = DateTime.UtcNow.AddDays(-30),
            IsActive = true,
        };
        db.Contributors.Add(contributor);
        await db.SaveChangesAsync();

        var contributorSqid = sqids.Encode(contributor.Id);
        var operatorSqid = sqids.Encode(230_002);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: operatorSqid, Roles: ["cnas-user"])));

        // Act — flag as insolvent.
        using var response = await client.PostAsync(
            $"/api/contributors/{contributorSqid}/insolvent",
            content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await response.Content.ReadAsStringAsync());

        // Assert — the flag is flipped.
        await using var readScope = _fixture.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var refreshed = await readDb.Contributors.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == contributor.Id);
        refreshed.Should().NotBeNull();
        refreshed!.IsInsolvent.Should().BeTrue(
            "MarkInsolventAsync must persist the IsInsolvent transition");

        // Assert — Critical audit row carries the SEC 042 envelope.
        var audit = await readDb.AuditLogs.AsNoTracking()
            .Where(a => a.EventCode == "CONTRIBUTOR.INSOLVENT_SET"
                && a.TargetEntity == nameof(Contributor)
                && a.TargetEntityId == contributor.Id)
            .SingleOrDefaultAsync();
        audit.Should().NotBeNull(
            "insolvency flag mutations are SEC 042 Critical events; the audit row must exist");
        audit!.Severity.Should().Be(AuditSeverity.Critical,
            "insolvency flips affect benefit-eligibility decisions — Critical by design");
        audit.ActorId.Should().Be(operatorSqid,
            "the audit ActorId must match the authenticated operator's Sqid id");
        audit.EventAtUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5),
            "the audit timestamp must be recent (sourced from ICnasTimeProvider)");
    }
}
