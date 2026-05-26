using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Cnas.Ps.Api.Controllers;
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
/// UC18 — "Administrarea utilizatorilor și a controlului de acces". End-to-end journey
/// covering an administrator listing users and granting a role through HTTP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Actors.</b> Administrator — authenticated CNAS staff with <c>cnas-admin</c> role.
/// The <c>UserAdministrationService</c> rejects callers lacking that role even though
/// the controller policy already enforces it (defense-in-depth per CLAUDE.md §5.4); the
/// E2E journey exercises both gates simultaneously by routing through HTTP.
/// </para>
/// <para>
/// <b>Business outcome asserted.</b>
/// <list type="number">
///   <item>HTTP 200 OK from <c>GET /api/users</c> with the target user present in the listing.</item>
///   <item>HTTP 204 No Content from <c>POST /api/users/{id}/roles/grant</c>.</item>
///   <item>The target <see cref="UserProfile.Roles"/> collection contains the new role.</item>
///   <item>An <see cref="AuditLog"/> entry with <c>EventCode = "USER.ROLE_GRANTED"</c>
///         exists, targeting the modified profile — UC23 wiring asserted in flight.</item>
/// </list>
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc18_UserAdminJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc18_UserAdminJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// An administrator lists users and grants the <c>cnas-decider</c> role to a target
    /// account. The role appears on subsequent reads of the profile and an audit
    /// <c>USER.ROLE_GRANTED</c> event is journaled.
    /// </summary>
    [Fact]
    public async Task GrantRole_AdminPersona_PersistsRoleAndAuditsAction()
    {
        // Arrange — seed a target user profile.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        var target = new UserProfile
        {
            MPassSubject = "uc18-target-sub",
            DisplayName = "UC18 Target User",
            Email = "uc18-target@example.test",
            Roles = ["cnas-user"],
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.UserProfiles.Add(target);
        await db.SaveChangesAsync();

        var targetSqid = sqids.Encode(target.Id);

        // Build the admin HTTP client.
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(700001), Roles: ["cnas-admin"])));

        // Act 1 — list users; the target must appear in the paged result.
        using var listResponse = await client.GetAsync("/api/users?page=1&pageSize=50");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            await listResponse.Content.ReadAsStringAsync());
        var page = await listResponse.Content.ReadFromJsonAsync<PagedResult<UserListItem>>();
        page.Should().NotBeNull();
        page!.Items.Should().Contain(u => u.Id == targetSqid,
            "the seeded user must appear in the paged listing");

        // Act 2 — grant the cnas-decider role to the target.
        using var grantResponse = await client.PostAsJsonAsync(
            $"/api/users/{targetSqid}/roles/grant",
            new GrantRoleRequest("cnas-decider"));
        grantResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await grantResponse.Content.ReadAsStringAsync());

        // Assert — role is persisted on the profile and an audit entry is journaled.
        await using var readScope = _fixture.Services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var refreshed = await readDb.UserProfiles.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == target.Id);
        refreshed.Should().NotBeNull();
        refreshed!.Roles.Should().Contain("cnas-decider", "the role grant must persist");
        refreshed.Roles.Should().Contain("cnas-user", "existing roles must not be wiped");

        var audit = await readDb.AuditLogs.AsNoTracking()
            .Where(a => a.EventCode == "USER.ROLE_GRANTED"
                && a.TargetEntity == nameof(UserProfile)
                && a.TargetEntityId == target.Id)
            .SingleOrDefaultAsync();
        audit.Should().NotBeNull(
            "granting a role is a Critical audit event per UC18 + SEC 042");
        audit!.Severity.Should().Be(AuditSeverity.Critical,
            "role mutations are Critical because they expand the principal's privileges");
    }
}

