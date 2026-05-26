using Cnas.Ps.Application.Backups;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.Backups;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — tests for <see cref="BackupOrchestrator"/>.
/// </summary>
public sealed class BackupOrchestratorTests
{
    [Fact]
    public async Task RunPolicy_HappyPath_PersistsSucceededRun_AndPassedIntegrityCheck()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out var codes);
        var target = new InMemoryBackupTarget();
        var orchestrator = BackupTestHelpers.NewOrchestrator(db, audit, new IBackupTarget[] { target });
        var policy = await BackupTestHelpers.SeedPolicyAsync(db);

        var result = await orchestrator.RunPolicyAsync($"SQID-{policy.Id}", BackupTriggerKind.Manual, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(BackupRunStatus.Succeeded.ToString());
        result.Value.PayloadHashSha256.Should().NotBeNull();
        db.BackupRuns.Should().HaveCount(1);
        db.BackupIntegrityChecks.Should().HaveCount(1);
        db.BackupIntegrityChecks.Single().Status.Should().Be(BackupIntegrityStatus.Passed);
        codes.Should().Contain(IBackupOrchestrator.AuditRunSucceeded);
    }

    [Fact]
    public async Task RunPolicy_TargetUploadFails_PersistsFailedRun_NoIntegrityRecord()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out var codes);

        var failingTarget = Substitute.For<IBackupTarget>();
        failingTarget.Kind.Returns(BackupTargetKind.InMemoryTest);
        failingTarget.UploadAsync(Arg.Any<BackupPolicy>(), Arg.Any<BackupPayloadStream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<BackupUploadResult>.Failure(
                IBackupTarget.TargetNotConfiguredCode,
                "Synthetic upload failure with sensitive value Foo<>; sanitised")));

        var orchestrator = BackupTestHelpers.NewOrchestrator(db, audit, new[] { failingTarget });
        var policy = await BackupTestHelpers.SeedPolicyAsync(db);

        var result = await orchestrator.RunPolicyAsync($"SQID-{policy.Id}", BackupTriggerKind.Manual, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(BackupRunStatus.Failed.ToString());
        result.Value.FailureReason.Should().NotBeNullOrWhiteSpace();
        db.BackupIntegrityChecks.Should().BeEmpty();
        codes.Should().Contain(IBackupOrchestrator.AuditRunFailed);
    }

    [Fact]
    public async Task RunPolicy_TargetHashMismatch_PersistsIntegrityFailed()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out var codes);

        // Target echoes a DIFFERENT hash on upload — the orchestrator should flip the run to IntegrityFailed.
        var mismatching = Substitute.For<IBackupTarget>();
        mismatching.Kind.Returns(BackupTargetKind.InMemoryTest);
        mismatching.UploadAsync(Arg.Any<BackupPolicy>(), Arg.Any<BackupPayloadStream>(), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(Result<BackupUploadResult>.Success(
                new BackupUploadResult("inmem/wrong-hash", c.Arg<BackupPayloadStream>().SizeBytes, "deadbeef".PadRight(64, '0')))));

        var orchestrator = BackupTestHelpers.NewOrchestrator(db, audit, new[] { mismatching });
        var policy = await BackupTestHelpers.SeedPolicyAsync(db);

        var result = await orchestrator.RunPolicyAsync($"SQID-{policy.Id}", BackupTriggerKind.Manual, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(BackupRunStatus.IntegrityFailed.ToString());
        db.BackupIntegrityChecks.Single().Status.Should().Be(BackupIntegrityStatus.Failed);
        codes.Should().Contain(IBackupOrchestrator.AuditIntegrityFailed);
    }

    [Fact]
    public async Task SweepExpired_Purges_Old_Runs_AndAuditsCount()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out var codes);
        var target = new InMemoryBackupTarget();
        var policy = await BackupTestHelpers.SeedPolicyAsync(db, retentionDays: 7);

        // Seed an expired run (StartedAt 30 days ago, payload still on target)
        var payloadResult = await target.UploadAsync(policy,
            new BackupPayloadStream(new byte[] { 1, 2, 3 }, InMemoryBackupTarget.ComputeSha256Hex(new byte[] { 1, 2, 3 }), 3),
            CancellationToken.None);
        payloadResult.IsSuccess.Should().BeTrue();

        var expiredRun = new BackupRun
        {
            PolicyId = policy.Id,
            RunNumber = "BKR-2026-000001",
            Status = BackupRunStatus.Succeeded,
            TriggerKind = BackupTriggerKind.Scheduled,
            StartedAt = BackupTestHelpers.ClockNow.AddDays(-30),
            CompletedAt = BackupTestHelpers.ClockNow.AddDays(-30),
            PayloadStorageKey = payloadResult.Value.StorageKey,
            PayloadSizeBytes = 3,
            PayloadHashSha256 = payloadResult.Value.Sha256Hex,
            CreatedAtUtc = BackupTestHelpers.ClockNow.AddDays(-30),
            CreatedBy = "system",
            IsActive = true,
        };
        db.BackupRuns.Add(expiredRun);
        await db.SaveChangesAsync();

        var orchestrator = BackupTestHelpers.NewOrchestrator(db, audit, new IBackupTarget[] { target });
        var result = await orchestrator.SweepExpiredRunsAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        var reloaded = await db.BackupRuns.FirstAsync();
        reloaded.RetentionPurgedAt.Should().NotBeNull();
        codes.Should().Contain(IBackupOrchestrator.AuditRetentionSwept);
    }

    [Fact]
    public async Task RetryIntegrityCheck_OnSucceededRun_Reverifies()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out _);
        var target = new InMemoryBackupTarget();
        var orchestrator = BackupTestHelpers.NewOrchestrator(db, audit, new IBackupTarget[] { target });
        var policy = await BackupTestHelpers.SeedPolicyAsync(db);

        var run = await orchestrator.RunPolicyAsync($"SQID-{policy.Id}", BackupTriggerKind.Manual, CancellationToken.None);
        run.IsSuccess.Should().BeTrue();

        var recheck = await orchestrator.RetryIntegrityCheckAsync(run.Value.Id, CancellationToken.None);
        recheck.IsSuccess.Should().BeTrue();
        recheck.Value.Status.Should().Be(BackupIntegrityStatus.Passed.ToString());
    }

    [Fact]
    public async Task GetRunById_UnknownSqid_ReturnsNotFound()
    {
        using var db = BackupTestHelpers.CreateContext();
        var audit = BackupTestHelpers.NewAuditCapturing(out _);
        var target = new InMemoryBackupTarget();
        var orchestrator = BackupTestHelpers.NewOrchestrator(db, audit, new IBackupTarget[] { target });

        var result = await orchestrator.GetRunByIdAsync("SQID-9999", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
