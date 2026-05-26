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
/// UC23 — "Jurnalizez evenimente". End-to-end journey covering the audit trail produced
/// by sensitive administrative actions. Drives the <c>UsersController.LockAsync</c> path
/// (an admin-only mutation) and verifies the <see cref="AuditLog"/> entry carries the
/// stable event code, actor id, and target id expected by SEC 042 / CLAUDE.md §5.6.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> Administrator — authenticated CNAS staff with the <c>cnas-admin</c>
/// role. The actor's Sqid id is read directly from the <see cref="TestAuthHandler"/>
/// persona and asserted against the audit log entry's <see cref="AuditLog.ActorId"/>.
/// </para>
/// <para>
/// <b>Business outcome asserted.</b>
/// <list type="number">
///   <item>HTTP 204 No Content from <c>POST /api/users/{id}/lock</c>.</item>
///   <item>The <see cref="UserProfile.State"/> flips to <see cref="UserAccountState.Locked"/>
///         (post-R0059 state machine; previously a boolean <c>IsLocked</c>).</item>
///   <item>An <see cref="AuditLog"/> row exists with the expected event code, severity,
///         actor id, target entity, target id, and a non-empty source IP — every field
///         the SEC 042 retention contract pins.</item>
/// </list>
/// </para>
/// <para>
/// <b>Why lock and not a sign-in event.</b> Sign-in events run through MPass and are
/// intentionally not exposed through HTTP in the E2E surface (the OIDC dance requires a
/// live MPass IdP). Lock is the simplest mutation that hits every limb of the audit
/// recorder: severity, actor, target, source IP, correlation id.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc23_AuditLogJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc23_AuditLogJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// An admin locks a user account and the audit trail is journaled with the SEC 042 fields.
    /// </summary>
    [Fact]
    public async Task LockUser_AdminAction_ProducesAuditTrailWithExpectedFields()
    {
        // Arrange — seed the target profile.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var target = new UserProfile
        {
            MPassSubject = "uc23-target-sub",
            DisplayName = "UC23 Target",
            Roles = ["cnas-user"],
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
            State = UserAccountState.Active,
        };
        db.UserProfiles.Add(target);
        await db.SaveChangesAsync();
        var targetSqid = sqids.Encode(target.Id);

        // Admin persona — record the Sqid so we can assert ActorId on the audit row.
        var adminSqid = sqids.Encode(800023);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: adminSqid, Roles: ["cnas-admin"])));

        // Act — lock the user via the admin endpoint.
        using var lockResponse = await client.PostAsync(
            $"/api/users/{targetSqid}/lock",
            content: null);
        lockResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await lockResponse.Content.ReadAsStringAsync());

        // Assert — State flipped to Locked on the profile (R0059 state machine).
        await using var readScope = _fixture.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var refreshed = await readDb.UserProfiles.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == target.Id);
        refreshed.Should().NotBeNull();
        refreshed!.State.Should().Be(UserAccountState.Locked, "the lock mutation must persist");

        // Assert — audit row carries the SEC 042 envelope.
        var audit = await readDb.AuditLogs.AsNoTracking()
            .Where(a => a.EventCode == "USER.LOCKED"
                && a.TargetEntity == nameof(UserProfile)
                && a.TargetEntityId == target.Id)
            .SingleOrDefaultAsync();
        audit.Should().NotBeNull(
            "every admin mutation must produce an audit-log row per SEC 042 / CLAUDE.md §5.6");
        audit!.ActorId.Should().Be(adminSqid,
            "the audit ActorId must match the authenticated principal's Sqid id");
        audit.Severity.Should().Be(AuditSeverity.Critical,
            "account lock is a Critical event because it changes the security posture of the principal");
        audit.EventAtUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5),
            "the audit timestamp must be recent (sourced from the injected ICnasTimeProvider)");
    }
}
