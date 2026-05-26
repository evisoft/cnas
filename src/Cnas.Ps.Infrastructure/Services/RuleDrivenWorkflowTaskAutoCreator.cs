using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Workflow;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0540 / TOR CF 05.01 (iter 134) — BPM-engine-independent workflow-task
/// auto-creator. Consults the <see cref="WorkflowAutoCreationRule"/> table on
/// every application status transition and stages one
/// <see cref="WorkflowTask"/> row per matching rule on the supplied
/// <see cref="ICnasDbContext"/> change tracker. The caller controls the
/// <c>SaveChanges</c> boundary so the transition + tasks commit atomically.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> CF 05.01 demands "auto-created tasks from workflow
/// definitions". The full workflow-definition-driven path is gated by the
/// external Operaton epic (R0120) which depends on ops/deploy work. To unblock
/// CF 05.01 ahead of that, this implementation consumes a small admin-editable
/// rule table — once Operaton lands, a second <see cref="IWorkflowTaskAutoCreator"/>
/// implementation replaces this one at the DI composition root and the rule
/// rows are soft-disabled.
/// </para>
/// <para>
/// <b>Lifetime.</b> Scoped — captures the per-request <see cref="ICnasDbContext"/>
/// (write-capable so we can stage rows) plus the injected
/// <see cref="ICnasTimeProvider"/> (drives <see cref="WorkflowTask.DueAtUtc"/>
/// computation deterministically under test).
/// </para>
/// <para>
/// <b>Failure semantics.</b> Genuine infrastructure errors surface through the
/// <see cref="Result{T}"/> failure branch. A missing application id or a
/// transition with no matching rule is NOT a failure — the auto-creator returns
/// an empty list and the calling state-machine writer continues.
/// </para>
/// </remarks>
public sealed class RuleDrivenWorkflowTaskAutoCreator(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ILogger<RuleDrivenWorkflowTaskAutoCreator>? logger = null) : IWorkflowTaskAutoCreator
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<RuleDrivenWorkflowTaskAutoCreator> _logger =
        logger ?? NullLogger<RuleDrivenWorkflowTaskAutoCreator>.Instance;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<WorkflowTask>>> OnApplicationTransitionAsync(
        long applicationId,
        ApplicationStatus from,
        ApplicationStatus to,
        CancellationToken cancellationToken = default)
    {
        // Fetch every active rule that matches the (from, to) transition. The
        // partial unique index on (From, To, TaskKind) WHERE IsActive ensures
        // that two active rules with the same kind cannot coexist, so the rows
        // returned here are unambiguous.
        var rules = await _db.WorkflowAutoCreationRules
            .Where(r => r.IsActive
                        && r.FromStatus == from
                        && r.ToStatus == to)
            .OrderBy(r => r.Id) // Stable order so test assertions are deterministic.
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rules.Count == 0)
        {
            // No rule matched — common path on transitions we have not configured
            // yet (e.g. terminal-to-terminal moves). Empty list, never a failure.
            return Result<IReadOnlyList<WorkflowTask>>.Success(Array.Empty<WorkflowTask>());
        }

        // Resolve the dossier id for the application (when known). The
        // auto-creator runs from BOTH pre-dossier sites (submit) and
        // post-dossier sites (advance). When the dossier exists we link the
        // task to it; when it does not yet exist we MUST refuse to stage the
        // tasks — using a 0L fallback would emit a foreign-key violation on
        // SaveChanges (DossierId → Dossiers.Id), which the caller swallows
        // silently and leaves the Draft→Submitted auto-task path dead.
        //
        // We resolve in a single round-trip outside the loop so multi-rule
        // transitions don't pay N round-trips for the same value.
        // TODO(workflow-auto): the caller (ApplicationServiceImpl.SubmitAsync /
        // ApplicationProcessingService.AdvanceAsync) should re-invoke the
        // auto-creator AFTER the dossier is materialised so the deferred tasks
        // actually land. Wiring is owned by the other agent batch; pin the
        // deferred-here behaviour below and add the post-dossier replay test
        // once that batch lands.
        var dossierId = await _db.Applications
            .Where(a => a.Id == applicationId)
            .Select(a => a.DossierId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (dossierId is null or 0L)
        {
            _logger.LogInformation(
                "WorkflowTaskAutoCreator: deferred for application {Id}; no dossier yet",
                applicationId);
            return Result<IReadOnlyList<WorkflowTask>>.Success(Array.Empty<WorkflowTask>());
        }

        var now = _clock.UtcNow;
        var created = new List<WorkflowTask>(rules.Count);
        foreach (var rule in rules)
        {
            var task = new WorkflowTask
            {
                DossierId = dossierId.Value,
                Title = $"{rule.TaskKind} (auto)",
                Status = WorkflowTaskStatus.Pending,
                AssignedUserId = null,
                GroupCode = rule.AssigneeRole,
                UnclaimedSinceUtc = now,
                DueAtUtc = now.AddDays(rule.DueWithinDays),
                CreatedAtUtc = now,
                CreatedBy = "system",
                IsActive = true,
            };
            _db.WorkflowTasks.Add(task);
            created.Add(task);
        }

        return Result<IReadOnlyList<WorkflowTask>>.Success(created);
    }
}
