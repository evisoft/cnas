using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Scheduling;

namespace Cnas.Ps.Infrastructure.Tests.Common;

/// <summary>
/// R2173 — test-only <see cref="IPeakHourGate"/> implementation that always
/// returns <see cref="PeakHourGateDecision.Allow"/>. Used by the existing job
/// test harnesses so the new gate dependency does not change observable
/// behaviour for tests that pre-date R2173.
/// </summary>
internal sealed class AllowAllPeakHourGate : IPeakHourGate
{
    /// <inheritdoc />
    public Task<PeakHourGateDecision> EvaluateAsync(string jobCode, CancellationToken cancellationToken)
        => Task.FromResult(PeakHourGateDecision.Allow);
}

/// <summary>
/// R2173 — test-only <see cref="IPeakHourGate"/> implementation that always
/// returns <see cref="PeakHourGateDecision.Skip"/>. Used by per-job tests that
/// assert the early-return semantics when the gate refuses a fire.
/// </summary>
internal sealed class AlwaysSkipPeakHourGate : IPeakHourGate
{
    /// <inheritdoc />
    public Task<PeakHourGateDecision> EvaluateAsync(string jobCode, CancellationToken cancellationToken)
        => Task.FromResult(PeakHourGateDecision.Skip);
}
