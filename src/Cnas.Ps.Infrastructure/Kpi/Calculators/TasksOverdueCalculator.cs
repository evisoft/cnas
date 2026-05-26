using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Kpi.Calculators;

/// <summary>
/// R0201 / TOR CF 20.02 — computes <c>Tasks.Overdue</c>: count of
/// <see cref="WorkflowTask"/> rows currently sitting in
/// <see cref="WorkflowTaskStatus.Overdue"/>. The status is flipped by the
/// dossier-SLA monitor job (R0934-adjacent) — this calculator is the
/// dashboard's read-side projection of that backlog.
/// </summary>
/// <remarks>
/// Emits a single scalar entry with value zero when the registry has no
/// overdue rows, so the dashboard's time series never has missing data
/// points. Operators interpret a flat zero as "the SLA monitor is doing
/// its job."
/// </remarks>
/// <param name="db">Read-only replica context resolved per fire.</param>
public sealed class TasksOverdueCalculator(IReadOnlyCnasDbContext db) : IKpiCalculator
{
    private readonly IReadOnlyCnasDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public string KpiCode => "Tasks.Overdue";

    /// <inheritdoc />
    public async Task<IReadOnlyList<KpiSnapshotEntry>> ComputeAsync(
        DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        var count = await _db.WorkflowTasks
            .Where(t => t.IsActive && t.Status == WorkflowTaskStatus.Overdue)
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        return new[]
        {
            new KpiSnapshotEntry(
                KpiCode: KpiCode,
                Value: count,
                ValueUnit: KpiValueUnits.Count,
                Dimension1: string.Empty,
                Dimension2: string.Empty),
        };
    }
}
