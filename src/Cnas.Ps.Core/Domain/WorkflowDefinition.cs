namespace Cnas.Ps.Core.Domain;

/// <summary>
/// UC16 — versioned BPMN/workflow definition. Each row is one immutable revision of the
/// workflow JSON for a given <see cref="Code"/>. The repository is append-only: a new
/// <c>SaveDefinitionAsync</c> never overwrites an existing row — it inserts a new row
/// with <see cref="Version"/> = previous + 1 and flips <see cref="IsCurrent"/> on the
/// previous current row to <c>false</c>. Historical revisions remain queryable for
/// audit, rollback, and (future) "show diff between versions" tooling.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identifier convention.</b> Unlike most external identifiers in CNAS the workflow
/// <see cref="Code"/> is NOT a Sqid (CLAUDE.md RULE 3 does not apply — see the
/// <c>WorkflowsController</c> XML doc for the same exception). The code IS the public
/// identifier — it is what an administrator types into the editor and what the
/// <see cref="ServicePassport.WorkflowCode"/> foreign-key-like reference points to.
/// Sqid-encoding would obscure the very label the workflow is known by, so we keep
/// the code transparent and store it case-normalised (upper-case invariant) in the
/// <c>Code</c> column.
/// </para>
/// <para>
/// <b>Immutable snapshot semantics.</b> Per CLAUDE.md cross-cutting "Immutable Snapshots",
/// each version captures the JSON payload at the instant of save. We never mutate
/// <see cref="DefinitionJson"/> on a persisted row — a "change" produces a new row.
/// This means a service passport's runtime behaviour for an in-flight application can
/// be reconstructed by joining against the workflow definition that was current at
/// application-submission time (future enhancement; today the engine reads the latest).
/// </para>
/// <para>
/// <b>Concurrency.</b> Inherits the Postgres <c>xmin</c> optimistic-concurrency token
/// from <see cref="AuditableEntity"/>. When two operators race to publish a new version
/// for the same code, the first <c>SaveChanges</c> wins; the second sees a
/// <c>DbUpdateConcurrencyException</c> when it tries to clear <see cref="IsCurrent"/> on
/// the previous row and the service translates that into
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.ConcurrencyConflict"/> so the caller can retry.
/// </para>
/// </remarks>
public sealed class WorkflowDefinition : AuditableEntity, IExternalCodeOwner
{
    /// <summary>
    /// Stable workflow identifier (e.g. <c>WF-PENSION-AGE</c>, <c>WF-INDEMNIZATION</c>).
    /// Human-readable kebab/SCREAMING_SNAKE chosen by administrators — NOT a Sqid.
    /// Canonicalised to upper-case invariant on write so lookups are case-insensitive.
    /// Carrying <see cref="IExternalCodeOwner"/> documents that this string is part of
    /// the public contract — <see cref="ServicePassport.WorkflowCode"/> points at it.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// Monotonically increasing version number for the workflow identified by
    /// <see cref="Code"/>. The first <c>SaveDefinitionAsync</c> for a code inserts
    /// <c>1</c>; each subsequent save inserts the previous maximum + 1. Together with
    /// <see cref="Code"/> this forms the natural key of the row (enforced by a unique
    /// index in the EF configuration).
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Full BPMN / workflow JSON payload as supplied by the administrator. Stored as
    /// PostgreSQL <c>text</c> — no size cap at the database layer because workflow
    /// definitions can carry rich annotations; payload-shape validation (well-formed
    /// JSON, structural rules) happens at the service layer before persistence.
    /// </summary>
    public required string DefinitionJson { get; set; }

    /// <summary>
    /// <c>true</c> for exactly one row per <see cref="Code"/> at any instant — the
    /// "current" revision served by <c>GetDefinitionAsync</c>. Older revisions remain
    /// in the table with <c>IsCurrent = false</c> for audit and rollback; they are
    /// still queryable by SQL or a future history endpoint. Distinct from the
    /// inherited <see cref="AuditableEntity.IsActive"/> soft-delete flag — a workflow
    /// row may be both <c>IsActive = true</c> (not soft-deleted) and
    /// <c>IsCurrent = false</c> (superseded by a newer version).
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// R0129 / CF 15.04 — FK to the row that superseded this revision. <c>null</c> on
    /// the currently-active row (<see cref="IsCurrent"/> = <c>true</c>) and on rows
    /// that have never been superseded; populated on history rows so the version chain
    /// can be followed forward by a single key lookup. Together with
    /// <see cref="SupersedesDefinitionId"/> these two columns form a doubly-linked list
    /// of versions per <see cref="Code"/>.
    /// </summary>
    public long? SupersededByDefinitionId { get; set; }

    /// <summary>
    /// R0129 / CF 15.04 — UTC timestamp at which this row stopped being the current
    /// revision (i.e. when a newer version was published). <c>null</c> on the active
    /// row; populated on the superseded row in the same transaction that flips
    /// <see cref="IsCurrent"/> to <c>false</c>. Drives diagnostic dashboards that
    /// surface "in-flight applications still pinned to a now-historical workflow".
    /// </summary>
    public DateTime? SupersededAtUtc { get; set; }

    /// <summary>
    /// R0129 / CF 15.04 — FK to the previous-version row in the chain. <c>null</c> on
    /// the very first version (<see cref="Version"/> = 1) and populated on every
    /// subsequent row. Together with <see cref="SupersededByDefinitionId"/> these two
    /// columns form a doubly-linked list of versions per <see cref="Code"/>.
    /// </summary>
    public long? SupersedesDefinitionId { get; set; }

    /// <summary>
    /// R0126 / CF 16.10 — workflow-scoped ACL: role codes (e.g. <c>cnas-decider</c>,
    /// <c>cnas-pension-clerk</c>) whose holders are allowed to act on tasks belonging to
    /// this workflow. Empty list means "fall back to the global controller-level role
    /// gates" — the ACL adds NOTHING beyond what the API surface already enforces, so a
    /// newly-published workflow keeps its legacy behaviour until an operator opts in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>AND-with-step-ACL.</b> <c>IWorkflowAclService</c> requires the caller's role
    /// set to intersect this list AND (when a <see cref="WorkflowStepAcl"/> row exists
    /// for the target step) the per-step <see cref="WorkflowStepAcl.RequiredRoles"/>.
    /// The two checks compose conjunctively — a workflow-level allow does not bypass a
    /// step-level requirement.
    /// </para>
    /// <para>
    /// <b>Super-admin escape hatch.</b> Holders of the global <c>cnas-tech-admin</c>
    /// role bypass every ACL check unconditionally (R0124/R0126 emergency-override
    /// invariant); the service-layer guard documents the role name.
    /// </para>
    /// </remarks>
    public List<string> AllowedRoles { get; set; } = new();

    /// <summary>
    /// R0126 / CF 16.10 — workflow-scoped ACL: group codes whose members are allowed to
    /// act on this workflow's tasks. Same "empty list = legacy fallback" semantics as
    /// <see cref="AllowedRoles"/>. Group membership lives on
    /// <see cref="UserProfile.Groups"/>; the ACL service materialises the intersection at
    /// resolve time.
    /// </summary>
    public List<string> AllowedGroups { get; set; } = new();

    /// <summary>
    /// R0124 / CF 16.08 — decision-engine rule-pack code evaluated by
    /// <c>IWorkflowRuleEngine.EvaluateStartAsync</c> when a workflow case begins (i.e.
    /// when an application is submitted that maps to this workflow). <c>null</c> means
    /// "no start-stage rules"; the engine treats the absence as an implicit ALLOW with
    /// no annotations. Non-null values must reference a known rule pack in the
    /// configured <c>IWorkflowRulePackEvaluator</c> store; an unknown pack code is
    /// treated as a block (<c>RULE_PACK_NOT_FOUND</c>) so a typo cannot silently let an
    /// unauthorised case proceed.
    /// </summary>
    public string? StartRulePackCode { get; set; }

    /// <summary>
    /// R0124 / CF 16.08 — decision-engine rule-pack code evaluated by
    /// <c>IWorkflowRuleEngine.EvaluateTransitionAsync</c> on every task transition
    /// (claim → in-progress, in-progress → complete, manual move to next step, etc.).
    /// <c>null</c> means "no transition-stage rules"; same fallback semantics as
    /// <see cref="StartRulePackCode"/>.
    /// </summary>
    public string? TransitionRulePackCode { get; set; }

    /// <summary>
    /// R0124 / CF 16.08 — decision-engine rule-pack code evaluated by
    /// <c>IWorkflowRuleEngine.EvaluateCompletionAsync</c> when the workflow's last task
    /// completes (i.e. the case is about to be closed). The engine's annotations are
    /// merged into the resulting decision document / audit row so post-flight rules can
    /// stamp derived fields (e.g. "RecomputedBenefitCategory"). <c>null</c> disables the
    /// completion-stage check.
    /// </summary>
    public string? CompletionRulePackCode { get; set; }

    /// <summary>
    /// R0671 / TOR CF 18.06 — stable category code grouping workflows by domain (e.g.
    /// <c>"pension"</c>, <c>"indemnization"</c>, <c>"allowance"</c>). Drives the
    /// access-scope filter so a staff user assigned to the pension category sees only
    /// workflow tasks anchored to pension workflows. <c>null</c> means "uncategorised";
    /// uncategorised workflows are visible to every scoped caller per the
    /// <c>Cnas.Ps.Application.Abstractions.IAccessScope</c> NULL-data semantics (cref is a
    /// plain string because Core may not reference Application).
    /// Capped at 64 chars at the persistence layer.
    /// </summary>
    public string? CategoryCode { get; set; }
}
