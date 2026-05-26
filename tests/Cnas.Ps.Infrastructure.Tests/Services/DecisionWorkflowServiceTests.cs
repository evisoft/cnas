using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="DecisionWorkflowService"/> (UC10 — approve / reject).
/// Uses EF Core InMemory for persistence and NSubstitute for collaborators (sqid, clock,
/// caller, audit). Exercises every <see cref="Result"/> branch in
/// <see cref="DecisionWorkflowService.ApproveAsync"/> and
/// <see cref="DecisionWorkflowService.RejectAsync"/>.
/// </summary>
public class DecisionWorkflowServiceTests
{
    /// <summary>Deterministic clock used across the suite so audit timestamps stay assertable.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Role required by <c>TransitionAsync</c>'s authorisation guard.</summary>
    private const string DeciderRole = "cnas-decider";

    /// <summary>Caller roles when the test wants the role guard to pass.</summary>
    private static readonly string[] DeciderRoles = [DeciderRole];

    /// <summary>Caller roles when the test wants the role guard to reject the call.</summary>
    private static readonly string[] NonDeciderRoles = ["cnas-examiner"];

    // ─────────────────────── ApproveAsync ───────────────────────

    [Fact]
    public async Task ApproveAsync_CallerLacksDeciderRole_ReturnsWorkflowNotDecider()
    {
        var harness = Harness.Create(roles: NonDeciderRoles);
        await harness.SeedAsync();

        var result = await harness.Service.ApproveAsync("DOSS-SQID", note: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowNotDecider);

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
    }

    [Fact]
    public async Task ApproveAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("bad").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.ApproveAsync("bad", note: "ok");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task ApproveAsync_DossierNotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(99999L));

        var result = await harness.Service.ApproveAsync("missing", note: "ok");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ApproveAsync_DossierWithoutApplication_ReturnsNotFound()
    {
        // The TransitionAsync guard treats `dossier.Application == null` as "not found" because
        // an orphaned dossier indicates corrupted state and cannot be acted on through this path.
        var harness = Harness.Create();
        // Seed a dossier whose ApplicationId points at a row that does not exist — the .Include()
        // will resolve dossier.Application to null and trip the guard.
        var dossier = new Dossier
        {
            CreatedAtUtc = ClockNow,
            ApplicationId = 9999L, // Intentionally dangling FK.
            DossierNumber = "D-2026-ORPHAN",
            IsActive = true,
        };
        harness.Db.Dossiers.Add(dossier);
        await harness.Db.SaveChangesAsync();
        harness.Sqids.TryDecode("DOSS-SQID").Returns(Result<long>.Success(dossier.Id));

        var result = await harness.Service.ApproveAsync("DOSS-SQID", note: "ok");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ApproveAsync_HappyPath_SetsApplicationApprovedAndDossierClosed_AuditsCritical()
    {
        // ASCII-only note so it round-trips through System.Text.Json's default escaper unchanged.
        // (Non-ASCII characters get \uXXXX-escaped, which would defeat a plain Contains assertion.)
        const string note = "Decision approved - all documents in order.";
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.ApproveAsync("DOSS-SQID", note);

        result.IsSuccess.Should().BeTrue();

        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Approved);
        app.UpdatedAtUtc.Should().Be(ClockNow);

        var dossier = await harness.Db.Dossiers.SingleAsync(d => d.Id == seeded.DossierId);
        dossier.ClosedAtUtc.Should().Be(ClockNow);
        dossier.UpdatedAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "DOSSIER.APPROVED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Dossier),
            seeded.DossierId,
            Arg.Is<string>(s => s.Contains(note, StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_NoteIsNull_StillRecordsAuditWithEmptyNote()
    {
        // A null note must serialize as `"note":""` rather than throw or short-circuit the audit.
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.ApproveAsync("DOSS-SQID", note: null);

        result.IsSuccess.Should().BeTrue();
        await harness.Audit.Received(1).RecordAsync(
            "DOSSIER.APPROVED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Dossier),
            seeded.DossierId,
            Arg.Is<string>(s => s.Contains("\"note\":\"\"", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── RejectAsync ───────────────────────

    [Fact]
    public async Task RejectAsync_EmptyReason_ThrowsArgumentException()
    {
        // The reason argument is a programmer-supplied invariant (CLAUDE.md §2.5 — input invariant
        // on a non-business-logic parameter). `ArgumentException.ThrowIfNullOrWhiteSpace` throws
        // synchronously before the Task is started, surfacing as a faulted Task on await.
        var harness = Harness.Create();
        await harness.SeedAsync();

        var act = async () => await harness.Service.RejectAsync("DOSS-SQID", reason: "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RejectAsync_CallerLacksDeciderRole_ReturnsWorkflowNotDecider()
    {
        var harness = Harness.Create(roles: NonDeciderRoles);
        await harness.SeedAsync();

        var result = await harness.Service.RejectAsync("DOSS-SQID", reason: "Documente lipsă.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.WorkflowNotDecider);
    }

    [Fact]
    public async Task RejectAsync_HappyPath_AlsoSetsDossierClosedAtUtc_AuditsCritical()
    {
        // Production fix applied: a rejected decision IS final, so the dossier must be closed
        // alongside the application. Previously `dossier.ClosedAtUtc = null` on reject left
        // closed-but-rejected dossiers counted as open in dashboard widgets.
        // ASCII-only reason — see note in ApproveAsync_HappyPath about JsonSerializer escaping.
        const string reason = "Missing civil-status documents; rejected.";
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.RejectAsync("DOSS-SQID", reason);

        result.IsSuccess.Should().BeTrue();

        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status.Should().Be(ApplicationStatus.Rejected);
        app.UpdatedAtUtc.Should().Be(ClockNow);

        var dossier = await harness.Db.Dossiers.SingleAsync(d => d.Id == seeded.DossierId);
        dossier.ClosedAtUtc.Should().Be(ClockNow);
        dossier.UpdatedAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "DOSSIER.REJECTED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Dossier),
            seeded.DossierId,
            Arg.Is<string>(s => s.Contains(reason, StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RejectAsync_LongReason_RecordedFully()
    {
        // Construct a 500-character reason and assert it is forwarded verbatim into the audit
        // payload (no truncation, no abbreviation). Using a deterministic seed keeps the
        // assertion reproducible across machines.
        var reason = new string('A', 500);
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();

        var result = await harness.Service.RejectAsync("DOSS-SQID", reason);

        result.IsSuccess.Should().BeTrue();
        await harness.Audit.Received(1).RecordAsync(
            "DOSSIER.REJECTED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Dossier),
            seeded.DossierId,
            Arg.Is<string>(s => s.Contains(reason, StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── iter-149: terminal-state guard ───────────────────────

    [Fact]
    public async Task ApproveAsync_AlreadyApproved_ReturnsConflict_AndDoesNotRewriteState()
    {
        // iter-149 — once the dossier reaches a terminal lifecycle state (Approved
        // / Rejected / Closed / Withdrawn) a second Approve/Reject call MUST
        // return Conflict instead of silently re-stamping ClosedAtUtc and
        // re-firing audit/notification/MCabinet/fallback side-effects.
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        // Flip the application to Approved (the verdict was already cast) and
        // capture the original closed-at so the second call cannot overwrite it.
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status = ApplicationStatus.Approved;
        var dossier = await harness.Db.Dossiers.SingleAsync(d => d.Id == seeded.DossierId);
        var originalClosedAt = new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Utc);
        dossier.ClosedAtUtc = originalClosedAt;
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.ApproveAsync("DOSS-SQID", note: "second approval attempt");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);

        // No audit row written and the dossier's ClosedAtUtc is untouched.
        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
        var reloaded = await harness.Db.Dossiers.SingleAsync(d => d.Id == seeded.DossierId);
        reloaded.ClosedAtUtc.Should().Be(originalClosedAt);
    }

    [Fact]
    public async Task RejectAsync_AlreadyRejected_ReturnsConflict()
    {
        var harness = Harness.Create();
        var seeded = await harness.SeedAsync();
        var app = await harness.Db.Applications.SingleAsync(a => a.Id == seeded.AppId);
        app.Status = ApplicationStatus.Rejected;
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.RejectAsync("DOSS-SQID", reason: "second rejection attempt");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-decision-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning a fixed instant for deterministic tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Identifiers of seeded entities returned to the test for assertion targeting.</summary>
    private sealed record SeedResult(long AppId, long DossierId, long SolicitantId, long PassportId);

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required DecisionWorkflowService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }
        public required ICnasTimeProvider Clock { get; init; }

        /// <summary>
        /// Wires the SUT with NSubstitute fakes and a fresh InMemory DB.
        /// </summary>
        /// <param name="roles">Roles granted to the simulated caller (default: decider).</param>
        public static Harness Create(IReadOnlyCollection<string>? roles = null)
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(roles ?? DeciderRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var service = new DecisionWorkflowService(
                db, sqids, clock, caller, audit, mcabinet, NullLogger<DecisionWorkflowService>.Instance);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Caller = caller,
                Sqids = sqids,
                Clock = clock,
            };
        }

        /// <summary>
        /// Seeds a coherent solicitant + passport + application + dossier graph and binds the
        /// canonical <c>"DOSS-SQID"</c> sqid → dossier.Id mapping the tests rely on.
        /// </summary>
        public async Task<SeedResult> SeedAsync()
        {
            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000007",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Ion Popescu",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-TEST",
                NameRo = "Test passport",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}",
                SnapshotJson = "{}",
                SubmittedAtUtc = ClockNow.AddDays(-1),
                ReferenceNumber = "PS-TEST-0001",
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = $"D-2026-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            app.DossierId = dossier.Id;
            await Db.SaveChangesAsync();

            Sqids.TryDecode("DOSS-SQID").Returns(Result<long>.Success(dossier.Id));

            return new SeedResult(app.Id, dossier.Id, solicitant.Id, passport.Id);
        }
    }
}
