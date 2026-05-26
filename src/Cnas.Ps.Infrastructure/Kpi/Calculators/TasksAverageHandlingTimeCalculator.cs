using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Kpi.Calculators;

/// <summary>
/// R0201 / TOR CF 20.02 — computes <c>Tasks.AvgHandlingHours</c>: average
/// elapsed-hours from <see cref="AuditableEntity.CreatedAtUtc"/> to
/// <see cref="WorkflowTask.CompletedAtUtc"/> over tasks completed in the
/// 7-day window ending at the snapshot-date exclusive end-of-day boundary.
/// Tasks with <c>CompletedAtUtc=null</c> are excluded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Look-back window.</b> Seven days captures a useful operator signal
/// without being so long that a single legacy slow case skews the average
/// indefinitely. Returns zero when no completed tasks exist in the window —
/// the dashboard renders this as "no data" rather than dropping the tile.
/// </para>
/// <para>
/// <b>Rounding.</b> The persisted value is rounded to four decimals to fit
/// the <c>numeric(20, 4)</c> column type configured in
/// <c>KpiSnapshotConfiguration</c>; this is sub-quarter-hour precision and
/// enough for the operator dashboard.
/// </para>
/// </remarks>
/// <param name="db">Read-only replica context resolved per fire.</param>
public sealed class TasksAverageHandlingTimeCalculator(IReadOnlyCnasDbContext db) : IKpiCalculator
{
    private readonly IReadOnlyCnasDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>The look-back window for the rolling average.</summary>
    private static readonly TimeSpan WindowSpan = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public string KpiCode => "Tasks.AvgHandlingHours";

    /// <inheritdoc />
    public async Task<IReadOnlyList<KpiSnapshotEntry>> ComputeAsync(
        DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        var dayEnd = snapshotDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1);
        var windowStart = dayEnd - WindowSpan;

        // Pull elapsed-tick pairs and compute the average in memory — Postgres
        // could do AVG(EXTRACT EPOCH ...) natively but the InMemory provider
        // used by tests cannot, and the per-day completed-task volume is
        // bounded by the working-day throughput (low thousands at most).
        var elapsedTicks = await _db.WorkflowTasks
            .Where(t => t.IsActive
                && t.CompletedAtUtc != null
                && t.CompletedAtUtc >= windowStart
                && t.CompletedAtUtc < dayEnd)
            .Select(t => (t.CompletedAtUtc!.Value - t.CreatedAtUtc).Ticks)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        decimal averageHours = 0m;
        if (elapsedTicks.Count > 0)
        {
            var avgTicks = elapsedTicks.Sum() / (double)elapsedTicks.Count;
            averageHours = decimal.Round(
                (decimal)TimeSpan.FromTicks((long)avgTicks).TotalHours, 4);
        }

        return new[]
        {
            new KpiSnapshotEntry(
                KpiCode: KpiCode,
                Value: averageHours,
                ValueUnit: KpiValueUnits.Hours,
                Dimension1: string.Empty,
                Dimension2: string.Empty),
        };
    }
}
