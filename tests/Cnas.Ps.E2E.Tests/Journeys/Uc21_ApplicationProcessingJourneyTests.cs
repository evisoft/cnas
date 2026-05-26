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
/// UC21 — "Procesare cerere/formular" (application processing). The TOR UC21 narrative
/// covers system-driven advancement of a cerere through its workflow lifecycle. The
/// Application layer ships an
/// <see cref="Cnas.Ps.Application.UseCases.IApplicationProcessingService.AdvanceAsync(string, System.Threading.CancellationToken)"/>
/// entry point intended for that "system actor" automation, but it is NOT wired to any
/// HTTP controller as of the #87 batch — see gap report.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pivot rationale.</b> Of the controllers shipped today
/// (<see cref="Cnas.Ps.Api.Controllers.ApplicationsController"/>,
/// <see cref="Cnas.Ps.Api.Controllers.ExaminationController"/>), the closest existing
/// surface for "transition a submitted cerere to a terminal state" is the
/// citizen-initiated withdraw endpoint
/// (<c>POST /api/applications/{id}/withdraw</c>). It is the only existing path that
/// advances a Submitted application to a closed status without depending on Examination's
/// dossier + verdict machinery (covered by UC08). The journey therefore exercises
/// withdrawal as the UC21 reference: a Submitted cerere is moved to a final state, the
/// application row records the transition, an audit row is journaled, and the response
/// shape matches the controller's no-content contract.
/// </para>
/// <para>
/// <b>Actors.</b> Solicitant — authenticated CNAS portal user; the underlying
/// <c>WithdrawAsync</c> service enforces that <c>app.SolicitantId == _caller.UserId</c>
/// at the service boundary (defense in depth — the controller has no role policy on this
/// endpoint, so the ownership check is the only gate).
/// </para>
/// <para>
/// <b>Business outcomes asserted.</b>
/// <list type="number">
///   <item>HTTP 204 No Content from <c>POST /api/applications/{id}/withdraw</c> when the
///         caller is the solicitant of an in-flight (Submitted) cerere.</item>
///   <item>The <see cref="ServiceApplication.Status"/> flips to
///         <see cref="ApplicationStatus.Withdrawn"/> and
///         <see cref="ServiceApplication.ClosedAtUtc"/> is stamped.</item>
///   <item>An <see cref="AuditLog"/> row with event code <c>APPLICATION.WITHDRAWN</c>
///         targets the cerere, names the solicitant Sqid as <c>ActorId</c>, and uses
///         <see cref="AuditSeverity.Notice"/> severity (lifecycle transition, not
///         security-critical).</item>
///   <item>HTTP 403 Forbidden when a different persona (not the owning solicitant)
///         attempts the withdraw — locks the BUG-021 fix that maps
///         <see cref="Cnas.Ps.Core.Common.ErrorCodes.Forbidden"/> to 403 rather than the
///         legacy uniform 400.</item>
/// </list>
/// </para>
/// <para>
/// <b>BUG-021 fix landed.</b> The controller now uses the same
/// <c>StatusForCode</c> translation pattern as
/// <see cref="Cnas.Ps.Api.Controllers.AdminController"/>:
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.Forbidden"/> → 403,
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.NotFound"/> → 404,
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.ApplicationLocked"/> → 409. The
/// non-owning-persona test below asserts the new 403 status, not the legacy uniform 400.
/// </para>
/// </remarks>
[Collection(AuthenticatedE2ECollection.Name)]
public sealed class Uc21_ApplicationProcessingJourneyTests
{
    private readonly AuthenticatedApiHostFixture _fixture;

    /// <summary>Injects the authenticated E2E host fixture.</summary>
    /// <param name="fixture">Shared collection fixture.</param>
    public Uc21_ApplicationProcessingJourneyTests(AuthenticatedApiHostFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A Solicitant withdraws their own submitted cerere. The application status flips to
    /// Withdrawn, <c>ClosedAtUtc</c> is stamped, and an <c>APPLICATION.WITHDRAWN</c> audit
    /// row is journaled with <c>Notice</c> severity.
    /// </summary>
    [Fact]
    public async Task Withdraw_OwningSolicitant_PersistsTransitionAndAudits()
    {
        // Arrange — seed a passport + a matched Solicitant/UserProfile pair so the
        // ApplicationServiceImpl.WithdrawAsync ownership check (SolicitantId == UserId)
        // passes. The UserProfile.Id is set explicitly to match Solicitant.Id (the same
        // pattern UC06 uses) so the caller's decoded Sqid id resolves identically.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        var passport = new ServicePassport
        {
            Code = "SP-UC21-E2E",
            NameRo = "Serviciu E2E UC21",
            DescriptionRo = "Pașaport seed pentru jurnalul UC21.",
            WorkflowCode = "wf-e2e",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        const string idnp = "2000000021001";

        // The in-memory DB is shared by every test in the AuthenticatedE2ECollection;
        // auto-generated identity values therefore drift across journeys and any pattern
        // that "re-uses Solicitant.Id as UserProfile.Id" risks a duplicate-key clash with
        // a UserProfile that another test has already seeded at the same auto-id.
        // Pinning both to a high constant in a UC21-owned range sidesteps the collision
        // without affecting the ownership check (which only requires
        // Solicitant.Id == UserProfile.Id == decoded caller Sqid).
        const long uc21OwnerId = 9_021_001L;
        var solicitant = new Solicitant
        {
            Id = uc21OwnerId,
            NationalId = idnp,
            NationalIdHash = hasher.ComputeHash(idnp),
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "UC21 E2E Solicitant",
            Email = "uc21-e2e@example.test",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);
        await db.SaveChangesAsync();

        // UserProfile re-uses the Solicitant primary key so the caller-context decoded
        // Sqid resolves to the same long id WithdrawAsync compares against.
        db.UserProfiles.Add(new UserProfile
        {
            Id = uc21OwnerId,
            MPassSubject = "uc21-e2e-sub",
            DisplayName = solicitant.DisplayName,
            Email = solicitant.Email,
            Roles = ["cnas-user"],
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var nowUtc = DateTime.UtcNow;
        var application = new ServiceApplication
        {
            SolicitantId = solicitant.Id,
            ServicePassportId = passport.Id,
            Status = ApplicationStatus.Submitted,
            FormPayloadJson = "{\"uc\":\"21\"}",
            SubmittedAtUtc = nowUtc.AddMinutes(-30),
            ReferenceNumber = $"UC21-{Guid.NewGuid():N}".Substring(0, 16),
            CreatedAtUtc = nowUtc.AddMinutes(-30),
            IsActive = true,
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();

        var solicitantSqid = sqids.Encode(solicitant.Id);
        var applicationSqid = sqids.Encode(application.Id);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: solicitantSqid, Roles: ["cnas-user"], Idnp: idnp)));

        // Act — withdraw the cerere.
        using var response = await client.PostAsync(
            $"/api/applications/{applicationSqid}/withdraw",
            content: null);

        // Assert — 204 No Content; the controller maps WithdrawAsync success to NoContent().
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            await response.Content.ReadAsStringAsync());

        // Assert — the application row reflects the Withdrawn transition.
        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var refreshed = await verifyDb.Applications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == application.Id);
        refreshed.Should().NotBeNull();
        refreshed!.Status.Should().Be(ApplicationStatus.Withdrawn,
            "WithdrawAsync must flip the cerere status to Withdrawn");
        refreshed.ClosedAtUtc.Should().NotBeNull(
            "WithdrawAsync must stamp ClosedAtUtc with the closure instant");
        refreshed.ClosedAtUtc!.Value.Should().BeAfter(nowUtc.AddMinutes(-5));

        // Assert — APPLICATION.WITHDRAWN audit row carries the SEC 042 envelope.
        var audit = await verifyDb.AuditLogs.AsNoTracking()
            .Where(a => a.EventCode == "APPLICATION.WITHDRAWN"
                && a.TargetEntity == nameof(ServiceApplication)
                && a.TargetEntityId == application.Id)
            .SingleOrDefaultAsync();
        audit.Should().NotBeNull(
            "withdrawal must produce an APPLICATION.WITHDRAWN audit row per SEC 042");
        audit!.Severity.Should().Be(AuditSeverity.Notice,
            "withdrawal is a lifecycle transition, not a security-critical event");
        audit.ActorId.Should().Be(solicitantSqid,
            "the audit ActorId must match the authenticated solicitant's Sqid id");
    }

    /// <summary>
    /// A non-owning persona attempting to withdraw someone else's cerere is rejected by
    /// the service-layer ownership check. The BUG-021 fix maps
    /// <see cref="ErrorCodes.Forbidden"/> to 403 (was: uniform 400 prior to this batch).
    /// </summary>
    [Fact]
    public async Task Withdraw_NonOwningPersona_ReturnsForbiddenAndPreservesState()
    {
        // Arrange — seed a passport + owning Solicitant/UserProfile + submitted cerere
        // (same shape as the happy-path test, but a fresh IDNP so the two journeys do
        // not interfere).
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();
        var hasher = scope.ServiceProvider.GetRequiredService<IDeterministicHasher>();

        var passport = new ServicePassport
        {
            Code = "SP-UC21B-E2E",
            NameRo = "Serviciu E2E UC21b",
            DescriptionRo = "Pașaport seed pentru jurnalul UC21b.",
            WorkflowCode = "wf-e2e",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        const string ownerIdnp = "2000000021002";

        // Both rows pinned to high constants for the same reason described in the
        // happy-path test — the shared in-memory DB makes auto-generated ids unreliable
        // across the collection.
        const long uc21bOwnerSolicitantId = 9_021_011L;
        var owner = new Solicitant
        {
            Id = uc21bOwnerSolicitantId,
            NationalId = ownerIdnp,
            NationalIdHash = hasher.ComputeHash(ownerIdnp),
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "UC21b Owner",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.Solicitants.Add(owner);
        await db.SaveChangesAsync();

        // Distinct UserProfile for the attacker persona — their UserId must resolve to a
        // different long than the cerere's SolicitantId, so the ownership check fails.
        // The Id is set explicitly to a high constant well outside any auto-generated
        // Solicitant identity-sequence range; relying on default identity assignment
        // risks an accidental collision because the Solicitants and UserProfiles tables
        // keep independent sequences in EF InMemory and prior collection tests may have
        // pushed the UserProfiles sequence to the same number the next Solicitant gets.
        const long attackerProfileId = 9_021_002L;
        var attacker = new UserProfile
        {
            Id = attackerProfileId,
            MPassSubject = "uc21b-attacker-sub",
            DisplayName = "UC21b Attacker",
            Email = "uc21b-attacker@example.test",
            Roles = ["cnas-user"],
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        };
        db.UserProfiles.Add(attacker);
        await db.SaveChangesAsync();

        var nowUtc = DateTime.UtcNow;
        var application = new ServiceApplication
        {
            SolicitantId = owner.Id,
            ServicePassportId = passport.Id,
            Status = ApplicationStatus.Submitted,
            FormPayloadJson = "{}",
            SubmittedAtUtc = nowUtc.AddMinutes(-10),
            ReferenceNumber = $"UC21B-{Guid.NewGuid():N}".Substring(0, 16),
            CreatedAtUtc = nowUtc.AddMinutes(-10),
            IsActive = true,
        };
        db.Applications.Add(application);
        await db.SaveChangesAsync();

        var attackerSqid = sqids.Encode(attacker.Id);
        var applicationSqid = sqids.Encode(application.Id);

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: attackerSqid, Roles: ["cnas-user"])));

        // Act — attacker attempts the withdraw.
        using var response = await client.PostAsync(
            $"/api/applications/{applicationSqid}/withdraw",
            content: null);

        // Assert — controller maps service-layer Forbidden to 403 per the BUG-021 fix.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());

        // Assert — the cerere remains Submitted; no audit row was written.
        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CnasDbContext>();
        var refreshed = await verifyDb.Applications.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == application.Id);
        refreshed.Should().NotBeNull();
        refreshed!.Status.Should().Be(ApplicationStatus.Submitted,
            "an unauthorized withdraw attempt must not mutate the cerere status");
        refreshed.ClosedAtUtc.Should().BeNull(
            "an unauthorized withdraw attempt must not stamp ClosedAtUtc");

        var auditExists = await verifyDb.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.EventCode == "APPLICATION.WITHDRAWN"
                && a.TargetEntityId == application.Id);
        auditExists.Should().BeFalse(
            "rejected withdraw attempts must NOT produce an APPLICATION.WITHDRAWN audit row");
    }

    /// <summary>
    /// A decider persona invokes the new <c>POST /api/applications/{id}/advance</c> endpoint
    /// against a non-existent Sqid; the controller maps the underlying
    /// <see cref="ErrorCodes.NotFound"/> to HTTP 404 (per the UC21 advance contract). Locks
    /// the wiring between the new controller action, the CnasDecider policy gate, and the
    /// failure-mapping helper.
    /// </summary>
    [Fact]
    public async Task Advance_DeciderPersona_UnknownApplicationSqid_Returns404()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(210_001), Roles: ["cnas-decider"])));

        // Act — Sqid encodes a primary key well above any seeded application.
        var unknownSqid = sqids.Encode(999_888_777L);
        using var response = await client.PostAsync(
            $"/api/applications/{unknownSqid}/advance", content: null);

        // Assert — service returns ErrorCodes.NotFound → controller maps to 404.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A <c>cnas-user</c> persona is rejected by the advance endpoint's CnasDecider policy
    /// gate. Locks the per-action authorization choice documented on
    /// <see cref="Cnas.Ps.Api.Controllers.ApplicationsController.AdvanceAsync"/>.
    /// </summary>
    [Fact]
    public async Task Advance_UserPersona_Returns403()
    {
        // Arrange
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sqids = scope.ServiceProvider.GetRequiredService<ISqidService>();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.HeaderName,
            TestPersonaHeader.Serialize(
                new TestPrincipal(Sub: sqids.Encode(210_002), Roles: ["cnas-user"])));

        // Act
        var anySqid = sqids.Encode(42L);
        using var response = await client.PostAsync(
            $"/api/applications/{anySqid}/advance", content: null);

        // Assert — CnasDecider policy rejects cnas-user with 403.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
    }
}
