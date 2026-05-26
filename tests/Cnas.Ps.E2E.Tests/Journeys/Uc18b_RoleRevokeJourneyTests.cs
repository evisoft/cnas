using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.E2E.Tests.Auth;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.E2E.Tests.Journeys;

/// <summary>
/// UC18 — "Administrarea utilizatorilor și a controlului de acces" (revoke branch).
/// Companion journey to <c>Uc18_UserAdminJourneyTests</c> (which covers grant) covering
/// the symmetric revoke path. Pivots away from the originally-suggested UC19 reports
/// journey because the system does not yet expose a reports HTTP endpoint — see the
/// final report for the pivot rationale.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> Administrator — authenticated CNAS staff with the <c>cnas-admin</c>
/// role. The <c>UserAdministrationService</c> re-checks the role at the service boundary
/// (defense-in-depth per CLAUDE.md §5.4) even though the controller policy already
/// enforces it; this journey exercises both gates by routing through HTTP.
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 204 No Content from <c>POST /api/users/{id}/roles/revoke</c>.</item>
///   <item>The target <see cref="UserProfile.Roles"/> collection no longer contains the
///         revoked role but retains the other ones (idempotent removal preserves the
///         remaining grants).</item>
///   <item>An <see cref="AuditLog"/> row with <c>EventCode = "USER.ROLE_REVOKED"</c>
///         exists, with <see cref="AuditSeverity.Critical"/> severity, targeting the
///         modified profile and naming the admin Sqid as <c>ActorId</c>.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc18b_RoleRevokeJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc18b_RoleRevokeJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// An administrator revokes a previously-held role from a target account. The role
    /// disappears from the profile, other roles persist, and a Critical
    /// <c>USER.ROLE_REVOKED</c> audit row is journaled.
    /// </summary>
    [Fact]
    public async Task RevokeRole_AdminPersona_RemovesRoleAndAuditsAction()
    {
        // Arrange — seed a target user profile holding multiple roles so we can assert
        // the revoke is surgical (only the named role is removed).
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var target = new UserProfile
        {
            MPassSubject = "uc18b-target-sub",
            DisplayName = "UC18b Target User",
            Email = "uc18b-target@example.test",
            Roles = ["cnas-user", "cnas-decider"],
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.UserProfiles.Add(target);
        await db.SaveChangesAsync();

        var targetSqid = sqids.Encode(target.Id);
        var adminSqid = sqids.Encode(180_002);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: adminSqid, Roles: ["cnas-admin"])));

        // Act — revoke the cnas-decider role.
        using var revokeResponse = await client.PostAsJsonAsync(
            $"/api/users/{targetSqid}/roles/revoke",
            new GrantRoleRequest("cnas-decider"));
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await revokeResponse.Content.ReadAsStringAsync());

        // Assert — role is removed but the remaining role is preserved.
        await using var readScope = _fixture.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var refreshed = await readDb.UserProfiles.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == target.Id);
        refreshed.Should().NotBeNull();
        refreshed!.Roles.Should().NotContain("cnas-decider", "the revoked role must be removed");
        refreshed.Roles.Should().Contain("cnas-user", "non-targeted roles must be preserved");

        var audit = await readDb.AuditLogs.AsNoTracking()
            .Where(a => a.EventCode == "USER.ROLE_REVOKED"
                && a.TargetEntity == nameof(UserProfile)
                && a.TargetEntityId == target.Id)
            .SingleOrDefaultAsync();
        audit.Should().NotBeNull(
            "role revocation must produce an audit-log row per CLAUDE.md §5.6 / SEC 042");
        audit!.Severity.Should().Be(AuditSeverity.Critical,
            "role mutations are Critical because they change the principal's privileges");
        audit.ActorId.Should().Be(adminSqid,
            "the audit ActorId must match the authenticated admin's Sqid id");
    }
}
