using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Workflow;

/// <summary>
/// R0540 / TOR CF 05.01 (iter 134) — strategy that auto-creates
/// <see cref="WorkflowTask"/> rows on application status transitions. Hooks into
/// every state-machine site (<c>ApplicationServiceImpl.SubmitAsync</c>,
/// <c>ApplicationProcessingService.AdvanceAsync</c>, decision-workflow advance
/// calls, etc.) so a transition Draft → Submitted (or Submitted →
/// UnderExamination, etc.) materialises the canonical workflow tasks for the
/// downstream actors.
/// </summary>
/// <remarks>
/// <para>
/// <b>BPM-engine-independent.</b> The shipped implementation
/// (<c>RuleDrivenWorkflowTaskAutoCreator</c>) consults a small admin-configurable
/// <c>WorkflowAutoCreationRule</c> table — NOT an external BPM engine — so CF
/// 05.01 lands before R0120's Operaton integration. Once Operaton is wired, a
/// second implementation (<c>OperatonWorkflowTaskAutoCreator</c>) replaces the
/// rule-driven default at the DI composition root and the rule table is
/// soft-disabled. Tests pin the contract so the swap-over is transparent.
/// </para>
/// <para>
/// <b>Failure semantics.</b> The auto-creator NEVER throws on a missing
/// precondition (no rule matches, application not found, …) — it returns an
/// empty list. Genuine infrastructure failures surface through the
/// <see cref="Result{T}"/> failure branch and the calling state-machine writer
/// logs a Warning + continues; the application transition itself is the source
/// of truth and MUST NOT be reverted just because the task creation failed.
/// </para>
/// <para>
/// <b>Atomicity.</b> The auto-creator participates in the SAME EF Core change
/// tracker as the caller. Implementations MUST NOT call <c>SaveChangesAsync</c>
/// themselves — the calling state-machine flushes the rows together with the
/// application row so the transition + tasks commit as one unit.
/// </para>
/// </remarks>
public interface IWorkflowTaskAutoCreator
{
    /// <summary>
    /// Inspects every rule that matches the (<paramref name="from"/>,
    /// <paramref name="to"/>) transition and stages one <see cref="WorkflowTask"/>
    /// row per matching rule. The created rows are added to the calling
    /// transaction's change tracker (NOT flushed); the caller controls the
    /// SaveChanges boundary.
    /// </summary>
    /// <param name="applicationId">Internal primary key of the
    /// <see cref="ServiceApplication"/> the transition belongs to (raw long; Sqid
    /// encoding lives at the API boundary per CLAUDE.md RULE 3).</param>
    /// <param name="from">Status the application is LEAVING.</param>
    /// <param name="to">Status the application is ENTERING.</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>Success wrapping the (possibly empty) list of staged tasks.
    /// Empty list = no rules matched the transition.</returns>
    Task<Result<IReadOnlyList<WorkflowTask>>> OnApplicationTransitionAsync(
        long applicationId,
        ApplicationStatus from,
        ApplicationStatus to,
        CancellationToken cancellationToken = default);
}
