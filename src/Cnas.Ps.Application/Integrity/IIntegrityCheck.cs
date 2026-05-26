using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Integrity;

/// <summary>
/// R2282 / TOR SEC 036 — pluggable invariant-rule contract. Each implementation
/// owns ONE invariant (e.g. "Claim.PaidAmount equals the sum of its payments")
/// and is resolved by the <c>IntegrityCheckJob</c> via
/// <c>IEnumerable&lt;IIntegrityCheck&gt;</c>. New checks are added by writing a
/// new class and registering it as Scoped — no central dispatch table edit
/// required.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only contract.</b> Every <see cref="IIntegrityCheck"/> implementation
/// receives a read-only DB context. The implementations MUST NEVER mutate
/// state — the goal of an integrity sweep is to observe + report, not to
/// silently "fix" things. Auto-remediation belongs to a separate workflow.
/// </para>
/// <para>
/// <b>Schema-tolerance.</b> Checks targeting an aggregate that may not have
/// shipped yet (e.g. a future <c>TreasuryReceiptDistribution</c> table) MUST
/// scan empty and report zero findings — never throw. The job suppresses
/// per-check failures into the run's <c>Failed</c> state only when an
/// unhandled exception escapes the registered set.
/// </para>
/// </remarks>
public interface IIntegrityCheck
{
    /// <summary>Stable check code recorded on every finding the check produces.</summary>
    string CheckCode { get; }

    /// <summary>Display name of the aggregate the check targets (e.g. <c>Claim</c>).</summary>
    string AggregateName { get; }

    /// <summary>Default severity assigned to findings unless the check overrides per-finding.</summary>
    IntegrityFindingSeverity Severity { get; }

    /// <summary>
    /// Runs the invariant check against the supplied context and returns
    /// the aggregated scan count + finding list.
    /// </summary>
    /// <param name="ctx">Read-only DB + clock envelope.</param>
    /// <param name="cancellationToken">Cancellation propagated from the Quartz fire.</param>
    /// <returns>The check's partial result.</returns>
    Task<IntegrityCheckPartialResult> RunAsync(IIntegrityCheckContext ctx, CancellationToken cancellationToken);
}
