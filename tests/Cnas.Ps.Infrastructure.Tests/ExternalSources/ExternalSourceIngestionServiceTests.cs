using Cnas.Ps.Application.ExternalSources;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services.ExternalSources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.ExternalSources;

/// <summary>
/// R0203 / TOR CF 20.06 — tests for <see cref="ExternalSourceIngestionService"/>.
/// Covers the manual-trigger path, the unknown-connector fallback path, and
/// the runs-list filter.
/// </summary>
public sealed class ExternalSourceIngestionServiceTests
{
    /// <summary>Manual trigger persists the run + emits the manual audit + the start counter.</summary>
    [Fact]
    public async Task TriggerManualRunAsync_HappyPath_PersistsRunAndAudits()
    {
        using var db = ExternalSourceTestHelpers.CreateContext();
        var audit = ExternalSourceTestHelpers.NewAuditCapturing(out var entries);
        var fallback = new InMemoryExternalSourceConnector();
        fallback.Seed("RSUD", new DateOnly(2026, 5, 24),
            new ExternalSourceFetchOutcomeDto(10, 8, 1, 1, "pull-1"));

        var svc = ExternalSourceTestHelpers.NewService(db, audit, fallback: fallback);

        var result = await svc.TriggerManualRunAsync("RSUD", new DateOnly(2026, 5, 24));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ExternalSourceIngestionStatus.Completed.ToString());
        result.Value.TotalRecordsPulled.Should().Be(10);
        result.Value.TotalRecordsApplied.Should().Be(8);
        result.Value.UpstreamPullId.Should().Be("pull-1");

        entries.Should().Contain(e => e.Code == IExternalSourceIngestionService.AuditManualTrigger);
        entries.Should().Contain(e => e.Code == IExternalSourceIngestionService.AuditRunCompleted);

        var persisted = await db.ExternalSourceIngestionRuns.SingleAsync();
        persisted.RunNumber.Should().StartWith("ESI-");
    }

    /// <summary>Invalid lower-case SourceCode is rejected at validation time.</summary>
    [Fact]
    public async Task TriggerManualRunAsync_InvalidSourceCode_ReturnsValidationFailure()
    {
        using var db = ExternalSourceTestHelpers.CreateContext();
        var audit = ExternalSourceTestHelpers.NewAuditCapturing(out _);
        var svc = ExternalSourceTestHelpers.NewService(db, audit);

        var result = await svc.TriggerManualRunAsync("rsp", asOfDate: null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);

        var rows = await db.ExternalSourceIngestionRuns.CountAsync();
        rows.Should().Be(0);
    }

    /// <summary>
    /// RSP connector failure (NOT_CONFIGURED placeholder) is captured as a
    /// Failed run row + Critical audit, not as a thrown exception.
    /// </summary>
    [Fact]
    public async Task TriggerManualRunAsync_RspConnectorFailure_PersistsFailedRun()
    {
        using var db = ExternalSourceTestHelpers.CreateContext();
        var audit = ExternalSourceTestHelpers.NewAuditCapturing(out var entries);
        var rsp = new RspExternalSourceConnector(Options.Create(new ExternalSourceOptions()));
        var svc = ExternalSourceTestHelpers.NewService(db, audit,
            connectors: new IExternalSourceConnector[] { rsp });

        var result = await svc.TriggerManualRunAsync("RSP", new DateOnly(2026, 5, 24));

        result.IsSuccess.Should().BeTrue(); // Lifecycle completed; terminal status Failed
        result.Value.Status.Should().Be(ExternalSourceIngestionStatus.Failed.ToString());
        result.Value.FailureReason.Should().Contain(RspExternalSourceConnector.NotConfiguredCode);

        entries.Should().Contain(e =>
            e.Code == IExternalSourceIngestionService.AuditRunFailed
            && e.Severity == AuditSeverity.Critical);
    }

    /// <summary>List filter by Status returns only matching rows.</summary>
    [Fact]
    public async Task ListRunsAsync_StatusFilter_ReturnsOnlyMatching()
    {
        using var db = ExternalSourceTestHelpers.CreateContext();
        db.ExternalSourceIngestionRuns.AddRange(
            new ExternalSourceIngestionRun
            {
                SourceCode = "RSP",
                RunNumber = "ESI-2026-000001",
                Status = ExternalSourceIngestionStatus.Completed,
                TriggerKind = ExternalSourceTriggerKind.Scheduled,
                StartedAtUtc = ExternalSourceTestHelpers.ClockNow.AddDays(-1),
                CompletedAtUtc = ExternalSourceTestHelpers.ClockNow.AddDays(-1).AddMinutes(2),
                CreatedAtUtc = ExternalSourceTestHelpers.ClockNow.AddDays(-1),
                IsActive = true,
            },
            new ExternalSourceIngestionRun
            {
                SourceCode = "RSP",
                RunNumber = "ESI-2026-000002",
                Status = ExternalSourceIngestionStatus.Failed,
                TriggerKind = ExternalSourceTriggerKind.Manual,
                StartedAtUtc = ExternalSourceTestHelpers.ClockNow,
                FailureReason = "EXT_SRC.RSP_NOT_CONFIGURED: placeholder",
                CreatedAtUtc = ExternalSourceTestHelpers.ClockNow,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var audit = ExternalSourceTestHelpers.NewAuditCapturing(out _);
        var svc = ExternalSourceTestHelpers.NewService(db, audit);

        var page = await svc.ListRunsAsync(new ExternalSourceIngestionRunFilterDto(Status: "Failed"));

        page.IsSuccess.Should().BeTrue();
        page.Value.Items.Should().ContainSingle()
            .Which.Status.Should().Be(ExternalSourceIngestionStatus.Failed.ToString());
    }

    /// <summary>Scheduled trigger uses the system actor and emits only the completion audit.</summary>
    [Fact]
    public async Task TriggerScheduledRunAsync_SystemActor_EmitsCompletionAudit()
    {
        using var db = ExternalSourceTestHelpers.CreateContext();
        var audit = ExternalSourceTestHelpers.NewAuditCapturing(out var entries);
        var fallback = new InMemoryExternalSourceConnector();
        var svc = ExternalSourceTestHelpers.NewService(db, audit, fallback: fallback);

        var result = await svc.TriggerScheduledRunAsync("RSUD", new DateOnly(2026, 5, 24));

        result.IsSuccess.Should().BeTrue();
        // Scheduled path does NOT emit the manual-trigger audit code.
        entries.Should().NotContain(e => e.Code == IExternalSourceIngestionService.AuditManualTrigger);
        entries.Should().Contain(e => e.Code == IExternalSourceIngestionService.AuditRunCompleted);
    }
}
