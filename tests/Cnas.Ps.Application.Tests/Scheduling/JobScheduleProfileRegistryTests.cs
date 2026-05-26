using Cnas.Ps.Application.Scheduling;

namespace Cnas.Ps.Application.Tests.Scheduling;

/// <summary>
/// R2173 / TOR PSR 004 — tests for the static
/// <see cref="JobScheduleProfileRegistry"/>. Pins the per-job mode choices so
/// future edits cannot accidentally re-classify a heavy-maintenance job as
/// <see cref="JobScheduleProfileMode.Anytime"/>.
/// </summary>
public sealed class JobScheduleProfileRegistryTests
{
    [Fact]
    public void Defaults_KpiSnapshot_IsOffPeakOnly()
    {
        JobScheduleProfileRegistry.Defaults[JobScheduleProfileRegistry.KpiSnapshot]
            .Mode.Should().Be(JobScheduleProfileMode.OffPeakOnly);
    }

    [Fact]
    public void Defaults_SiemForwarder_IsAlways()
    {
        JobScheduleProfileRegistry.Defaults[JobScheduleProfileRegistry.SiemForwarder]
            .Mode.Should().Be(JobScheduleProfileMode.Always);
    }

    [Fact]
    public void Defaults_TreasuryDistribution_IsAnytime()
    {
        // Financial pipeline must run continuously — TreasuryDistribution must
        // be classified Anytime regardless of off-peak preference for ETL.
        JobScheduleProfileRegistry.Defaults[JobScheduleProfileRegistry.TreasuryDistribution]
            .Mode.Should().Be(JobScheduleProfileMode.Anytime);
    }

    [Fact]
    public void Defaults_AdminActionBacklogObserver_IsAnytime()
    {
        JobScheduleProfileRegistry.Defaults[JobScheduleProfileRegistry.AdminActionBacklogObserver]
            .Mode.Should().Be(JobScheduleProfileMode.Anytime);
    }

    [Fact]
    public void Defaults_ContainsAllDeclaredJobCodeConstants()
    {
        // Sanity check — the dictionary should carry one entry per declared
        // constant so the public API and the runtime data stay in lockstep.
        JobScheduleProfileRegistry.Defaults.Should().ContainKeys(
            JobScheduleProfileRegistry.KpiSnapshot,
            JobScheduleProfileRegistry.ContributorPeriodProjection,
            JobScheduleProfileRegistry.AuditArchiveReplay,
            JobScheduleProfileRegistry.SiemForwarder,
            JobScheduleProfileRegistry.SecurityAlertEvaluator,
            JobScheduleProfileRegistry.AdminActionBacklogObserver,
            JobScheduleProfileRegistry.TreasuryDistribution,
            JobScheduleProfileRegistry.ReportJobBackground,
            JobScheduleProfileRegistry.WorkflowNotificationStrategyCacheRefresh,
            JobScheduleProfileRegistry.PenaltyRepaymentDefaultDetection,
            JobScheduleProfileRegistry.DailyBassReceiptsSummary,
            JobScheduleProfileRegistry.SessionAutoLock,
            JobScheduleProfileRegistry.BulkSelectionCleanup,
            JobScheduleProfileRegistry.UserAbsenceLifecycle);
    }
}
