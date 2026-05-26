using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.Integrity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Integrity;
using Cnas.Ps.Infrastructure.Services.Integrity.Checks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Integrity;

/// <summary>
/// R2282 / TOR SEC 036 — service-level tests for
/// <see cref="IntegrityCheckService"/>. Verifies the manual-run lifecycle,
/// the acknowledgement audit, and the open-findings projection.
/// </summary>
public sealed class IntegrityCheckServiceTests
{
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    private static IAuditService NewAudit(out List<string> capturedCodes)
    {
        var codes = new List<string>();
        capturedCodes = codes;
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                codes.Add(call.ArgAt<string>(0));
                return Task.FromResult(Result.Success());
            });
        return audit;
    }

    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("USR-1");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-integrity");
        return caller;
    }

    private static IntegrityCheckService NewService(
        CnasDbContext db,
        IEnumerable<IIntegrityCheck> checks,
        IAuditService audit)
        => new(
            db: db,
            checkContext: IntegrityTestHelpers.WrapContext(db),
            checks: checks,
            audit: audit,
            sqids: NewSqidMock(),
            clock: new IntegrityTestHelpers.StubClock(IntegrityTestHelpers.ClockNow),
            caller: NewCaller(),
            filterValidator: new IntegrityFindingFilterValidator(),
            ackValidator: new IntegrityFindingAcknowledgeInputValidator());

    [Fact]
    public async Task StartManualRunAsync_HappyPath_PersistsCompletedRunAndAuditsCritical()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var audit = NewAudit(out var codes);
        var svc = NewService(db, new IIntegrityCheck[]
        {
            new UserProfileNationalIdHashSyncCheck(),
        }, audit);

        var result = await svc.StartManualRunAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(IntegrityCheckRunStatus.Completed));
        result.Value.TriggerKind.Should().Be(nameof(IntegrityCheckTriggerKind.Manual));
        result.Value.RunCompletedAt.Should().NotBeNull();
        var stored = await db.IntegrityCheckRuns.SingleAsync();
        stored.Status.Should().Be(IntegrityCheckRunStatus.Completed);
        codes.Should().Contain("INTEGRITY_CHECK.MANUAL_RUN_STARTED");
    }

    [Fact]
    public async Task StartManualRunAsync_PersistsFindings_WhenInvariantBreached()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        // Seed a row that violates the IDNP-hash invariant.
        db.UserProfiles.Add(new UserProfile
        {
            DisplayName = "Bob",
            NationalId = "2000000000008",
            NationalIdHash = null,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        });
        await db.SaveChangesAsync();

        var svc = NewService(db, new IIntegrityCheck[]
        {
            new UserProfileNationalIdHashSyncCheck(),
        }, NewAudit(out _));

        var result = await svc.StartManualRunAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalFindings.Should().Be(1);
        result.Value.FindingsBySeverity[nameof(IntegrityFindingSeverity.Critical)].Should().Be(1);
        var findings = await db.IntegrityCheckFindings.ToListAsync();
        findings.Should().HaveCount(1);
        findings[0].Severity.Should().Be(IntegrityFindingSeverity.Critical);
    }

    [Fact]
    public async Task AcknowledgeFindingAsync_StampsNoteAndEmitsAuditCritical()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var run = new IntegrityCheckRun
        {
            RunStartedAt = IntegrityTestHelpers.ClockNow,
            RunCompletedAt = IntegrityTestHelpers.ClockNow,
            TriggerKind = IntegrityCheckTriggerKind.Scheduled,
            Status = IntegrityCheckRunStatus.Completed,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        };
        db.IntegrityCheckRuns.Add(run);
        await db.SaveChangesAsync();
        var finding = new IntegrityCheckFinding
        {
            RunId = run.Id,
            CheckCode = "USER_PROFILE.NATIONAL_ID_HASH_MISSING",
            Severity = IntegrityFindingSeverity.Critical,
            AggregateName = "UserProfile",
            AggregateRowId = 42L,
            Description = "Missing IDNP hash.",
            FirstDetectedAt = IntegrityTestHelpers.ClockNow,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        };
        db.IntegrityCheckFindings.Add(finding);
        await db.SaveChangesAsync();

        var audit = NewAudit(out var codes);
        var svc = NewService(db, Array.Empty<IIntegrityCheck>(), audit);

        var result = await svc.AcknowledgeFindingAsync(
            $"SQID-{finding.Id}",
            new IntegrityFindingAcknowledgeInputDto("Investigated — confirmed broken import."));

        result.IsSuccess.Should().BeTrue();
        result.Value.Acknowledged.Should().BeTrue();
        result.Value.AcknowledgementNote.Should().Be("Investigated — confirmed broken import.");
        result.Value.AcknowledgedByUserSqid.Should().NotBeNullOrEmpty();
        codes.Should().Contain("INTEGRITY_CHECK.FINDING_ACKNOWLEDGED");
    }

    [Fact]
    public async Task ListOpenFindingsAsync_ExcludesAcknowledgedByDefault()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var run = new IntegrityCheckRun
        {
            RunStartedAt = IntegrityTestHelpers.ClockNow,
            RunCompletedAt = IntegrityTestHelpers.ClockNow,
            TriggerKind = IntegrityCheckTriggerKind.Scheduled,
            Status = IntegrityCheckRunStatus.Completed,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        };
        db.IntegrityCheckRuns.Add(run);
        await db.SaveChangesAsync();
        db.IntegrityCheckFindings.Add(new IntegrityCheckFinding
        {
            RunId = run.Id,
            CheckCode = "TEST.OPEN",
            Severity = IntegrityFindingSeverity.High,
            AggregateName = "Test",
            AggregateRowId = 1,
            Description = "open",
            FirstDetectedAt = IntegrityTestHelpers.ClockNow,
            Acknowledged = false,
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        });
        db.IntegrityCheckFindings.Add(new IntegrityCheckFinding
        {
            RunId = run.Id,
            CheckCode = "TEST.ACK",
            Severity = IntegrityFindingSeverity.High,
            AggregateName = "Test",
            AggregateRowId = 2,
            Description = "ack",
            FirstDetectedAt = IntegrityTestHelpers.ClockNow,
            Acknowledged = true,
            AcknowledgedAt = IntegrityTestHelpers.ClockNow,
            AcknowledgementNote = "noted",
            CreatedAtUtc = IntegrityTestHelpers.ClockNow,
        });
        await db.SaveChangesAsync();

        var svc = NewService(db, Array.Empty<IIntegrityCheck>(), NewAudit(out _));
        var result = await svc.ListOpenFindingsAsync(new IntegrityFindingFilterDto(
            Severity: null, AggregateName: null, CheckCode: null,
            OnlyOpen: true, Skip: 0, Take: 50));

        result.IsSuccess.Should().BeTrue();
        result.Value.Total.Should().Be(1);
        result.Value.Items.Should().ContainSingle(i => i.CheckCode == "TEST.OPEN");
    }

    /// <summary>
    /// R2709 / TOR SEC 053 — the service must pick up
    /// <see cref="AuditChainIntegrityCheck"/> when it's registered alongside
    /// the existing checks. The verifier returns a clean report → no findings
    /// are persisted, but the run completes successfully and the audit code
    /// fires (same lifecycle as every other check).
    /// </summary>
    [Fact]
    public async Task StartManualRunAsync_IncludesAuditChainCheck_HappyPath()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var verifier = Substitute.For<IAuditChainVerifier>();
        verifier
            .VerifyAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AuditChainVerificationReport>.Success(
                new AuditChainVerificationReport(
                    IsValid: true,
                    CheckedCount: 3,
                    FirstBrokenRowId: null,
                    FirstBrokenReason: null))));

        var svc = NewService(db, new IIntegrityCheck[]
        {
            new AuditChainIntegrityCheck(verifier),
        }, NewAudit(out var codes));

        var result = await svc.StartManualRunAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(IntegrityCheckRunStatus.Completed));
        result.Value.TotalFindings.Should().Be(0);
        codes.Should().Contain("INTEGRITY_CHECK.MANUAL_RUN_STARTED");
    }

    /// <summary>
    /// R2709 / TOR SEC 053 — when the verifier reports a broken chain, the
    /// service must persist exactly one Critical finding under the
    /// <c>AUDIT_LOG.CHAIN</c> check code, with the broken row id surfaced
    /// through <c>AggregateRowId</c>.
    /// </summary>
    [Fact]
    public async Task StartManualRunAsync_AuditChainBroken_PersistsCriticalFinding()
    {
        using var db = IntegrityTestHelpers.CreateContext();
        var verifier = Substitute.For<IAuditChainVerifier>();
        verifier
            .VerifyAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<AuditChainVerificationReport>.Success(
                new AuditChainVerificationReport(
                    IsValid: false,
                    CheckedCount: 7,
                    FirstBrokenRowId: 123L,
                    FirstBrokenReason: "RowHashMismatch"))));

        var svc = NewService(db, new IIntegrityCheck[]
        {
            new AuditChainIntegrityCheck(verifier),
        }, NewAudit(out _));

        var result = await svc.StartManualRunAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalFindings.Should().Be(1);
        result.Value.FindingsBySeverity[nameof(IntegrityFindingSeverity.Critical)].Should().Be(1);
        var findings = await db.IntegrityCheckFindings.ToListAsync();
        findings.Should().HaveCount(1);
        findings[0].CheckCode.Should().Be("AUDIT_LOG.CHAIN");
        findings[0].Severity.Should().Be(IntegrityFindingSeverity.Critical);
        findings[0].AggregateName.Should().Be("AuditLog");
        findings[0].AggregateRowId.Should().Be(123L);
        findings[0].Description.Should().Contain("RowHashMismatch");
    }
}
