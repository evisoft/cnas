using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0540 / TOR CF 05.01 (iter 134) — canonical default rule-set seeded into a
/// fresh environment so the rule-driven auto-creator has something to fire on
/// out of the box. The four rules cover the canonical application lifecycle:
/// <list type="number">
///   <item><c>Draft → Submitted</c>: initial-review task for the registrar group;</item>
///   <item><c>Submitted → UnderExamination</c>: examination task for the examiner group;</item>
///   <item><c>UnderExamination → PendingApproval</c>: approval task for the decider group;</item>
///   <item><c>Approved → Closed</c>: payment-dispatch task for the MPay operator group.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The seed list is intentionally code-resident (not <c>HasData</c>) so
/// operators can soft-disable any row without a migration when the future
/// Operaton-driven path (R0120) replaces this implementation. A small admin
/// CRUD surface lands separately when the corresponding TOR clause is opened.
/// </para>
/// <para>
/// Each rule's <see cref="WorkflowAutoCreationRule.DueWithinDays"/> mirrors the
/// SLA window typical for the corresponding stage:
/// 1-day initial review,
/// 7-day examination,
/// 14-day decider approval,
/// 3-day payment dispatch.
/// </para>
/// </remarks>
public static class DefaultWorkflowAutoCreationRules
{
    /// <summary>
    /// Returns the canonical seed list. Each call returns a fresh list so the
    /// caller may safely tweak <see cref="AuditableEntity.CreatedAtUtc"/> /
    /// <see cref="AuditableEntity.CreatedBy"/> before persisting.
    /// </summary>
    /// <param name="nowUtc">UTC timestamp to stamp on every seeded row.</param>
    /// <param name="createdBy">Audit attribution string for the seed run.</param>
    public static IReadOnlyList<WorkflowAutoCreationRule> Build(DateTime nowUtc, string createdBy = "system-seed") =>
    [
        new WorkflowAutoCreationRule
        {
            FromStatus = ApplicationStatus.Draft,
            ToStatus = ApplicationStatus.Submitted,
            TaskKind = "INITIAL_REVIEW",
            AssigneeRole = "cnas-registrar",
            DueWithinDays = 1,
            CreatedAtUtc = nowUtc,
            CreatedBy = createdBy,
            IsActive = true,
        },
        new WorkflowAutoCreationRule
        {
            FromStatus = ApplicationStatus.Submitted,
            ToStatus = ApplicationStatus.UnderExamination,
            TaskKind = "EXAMINATION",
            AssigneeRole = "cnas-examiner",
            DueWithinDays = 7,
            CreatedAtUtc = nowUtc,
            CreatedBy = createdBy,
            IsActive = true,
        },
        new WorkflowAutoCreationRule
        {
            FromStatus = ApplicationStatus.UnderExamination,
            ToStatus = ApplicationStatus.PendingApproval,
            TaskKind = "DECIDER_APPROVAL",
            AssigneeRole = "cnas-decider",
            DueWithinDays = 14,
            CreatedAtUtc = nowUtc,
            CreatedBy = createdBy,
            IsActive = true,
        },
        new WorkflowAutoCreationRule
        {
            FromStatus = ApplicationStatus.Approved,
            ToStatus = ApplicationStatus.Closed,
            TaskKind = "PAYMENT_DISPATCH",
            AssigneeRole = "cnas-mpay-operator",
            DueWithinDays = 3,
            CreatedAtUtc = nowUtc,
            CreatedBy = createdBy,
            IsActive = true,
        },
    ];
}
