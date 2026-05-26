namespace Cnas.Ps.Contracts;

/// <summary>
/// One row of the pending admin actions list (R0058 / SEC 027). All identifiers are
/// Sqid-encoded per CLAUDE.md RULE 3; the maker's identity is surfaced as the
/// <c>UserProfile</c> Sqid only — never as IDNP, email, or display name — so checkers
/// cannot deanonymise activity from the queue itself.
/// </summary>
/// <param name="Id">Sqid-encoded id of the pending action row.</param>
/// <param name="Operation">Stable operation code (e.g. <c>USER.SUSPEND</c>) the executor will run on approval.</param>
/// <param name="MakerUserId">Sqid-encoded <c>UserProfile.Id</c> of the administrator that submitted the action.</param>
/// <param name="MakerRequestedAtUtc">UTC instant at which the maker submitted the action.</param>
/// <param name="ExpiresAtUtc">UTC instant after which the action auto-expires.</param>
public sealed record PendingAdminActionItem(
    string Id,
    string Operation,
    string MakerUserId,
    DateTime MakerRequestedAtUtc,
    DateTime ExpiresAtUtc);

/// <summary>
/// Request body for <c>POST /api/admin/pending-actions/{id}/reject</c>. The reason is
/// persisted on the pending row and surfaces in the audit trail so an investigator can
/// later understand why a checker declined to approve.
/// </summary>
/// <param name="Reason">Free-form rejection reason; capped at 512 chars by the EF mapping.</param>
public sealed record RejectAdminActionRequest(string Reason);
