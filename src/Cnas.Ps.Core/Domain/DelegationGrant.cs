using Cnas.Ps.Core.Audit;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — time-bounded permission grant under which one user
/// (the <i>grantor</i>) authorises another user (the <i>delegatee</i>) to act on their
/// behalf for the duration of the <see cref="ValidFromUtc"/>..<see cref="ValidToUtc"/>
/// window. The grant carries a free-text <see cref="Scope"/> (e.g.
/// <c>"approve.executory_documents"</c>) so different downstream services can scope
/// authorisation checks to the slice of the grantor's rights that the delegatee actually
/// inherits.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the grant's id is
/// surfaced to API callers as a Sqid-encoded string (<c>DelegationGrantDto.Id</c>)
/// per CLAUDE.md RULE 3 / ARH 027. The raw <see cref="AuditableEntity.Id"/> never
/// leaves the system.
/// </para>
/// <para>
/// <b>Suspends grantor rights.</b> When <see cref="SuspendsGrantorRights"/> is
/// <c>true</c>, downstream authorisation gates MUST treat the grantor as having
/// surrendered the rights covered by <see cref="Scope"/> for the duration of the
/// window — only the delegatee can exercise them. Used when a senior approver hands a
/// pending decision queue to a deputy and wants to guarantee that two parallel
/// approvals cannot land on the same record.
/// </para>
/// <para>
/// <b>Revocation.</b> Both the grantor and an administrator may revoke a grant before
/// its natural expiry via <c>IDelegationLifecycleService.RevokeAsync</c>. Revocation
/// stamps <see cref="RevokedAtUtc"/> + <see cref="RevokeReason"/> but the row is NEVER
/// hard-deleted — it stays in the table for the audit trail (SEC 042) so an investigator
/// can later reconstruct who acted on behalf of whom and for how long.
/// </para>
/// <para>
/// <b>Active grant predicate.</b> A grant is considered <i>active at instant T</i> when
/// <c>IsActive == true</c> AND <c>ValidFromUtc &lt;= T &lt;= ValidToUtc</c> AND
/// <c>RevokedAtUtc IS NULL</c>. The list-active service path applies the same predicate
/// in SQL via the <c>(GrantorUserId, ValidFromUtc, ValidToUtc, RevokedAtUtc)</c>
/// composite index.
/// </para>
/// <para>
/// <b>Audit + soft-delete.</b> Inherits the standard auditing fields
/// (<see cref="AuditableEntity.CreatedAtUtc"/>, <see cref="AuditableEntity.UpdatedAtUtc"/>,
/// <see cref="AuditableEntity.IsActive"/>, <see cref="AuditableEntity.Xmin"/>) from
/// <see cref="AuditableEntity"/>. Hard delete is reserved for GDPR right-to-erasure
/// requests; ordinary revocation is a soft mutation.
/// </para>
/// </remarks>
[AutoAudit(Severity = AuditSeverity.Notice, EventCodePrefix = "DELEGATION")]
public sealed class DelegationGrant : AuditableEntity, IExternalId
{
    /// <summary>
    /// Raw <c>UserProfile.Id</c> of the user that issued the grant — the principal whose
    /// rights are being delegated. Stable across the row's lifetime; never updated.
    /// </summary>
    public long GrantorUserId { get; set; }

    /// <summary>
    /// Raw <c>UserProfile.Id</c> of the user receiving the delegated rights. MUST differ
    /// from <see cref="GrantorUserId"/> — self-delegation is rejected at the service
    /// boundary because it provides no business value and complicates the audit trail.
    /// </summary>
    public long DelegateeUserId { get; set; }

    /// <summary>Inclusive UTC start of the delegation window.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>
    /// Exclusive UTC end of the delegation window. The service validates that
    /// <see cref="ValidToUtc"/> &gt; <see cref="ValidFromUtc"/> and that the window
    /// fits inside the 90-day operational cap (R0057 / SEC 026).
    /// </summary>
    public DateTime ValidToUtc { get; set; }

    /// <summary>
    /// When <c>true</c>, downstream authorisation gates MUST treat the grantor as
    /// having surrendered the rights covered by <see cref="Scope"/> for the duration
    /// of the window. See class-level remarks.
    /// </summary>
    public bool SuspendsGrantorRights { get; set; }

    /// <summary>
    /// Free-text scope discriminator captured at grant time. Currently parsed by
    /// downstream services on a per-feature basis (no central scope vocabulary yet);
    /// validators only assert non-empty and a length cap. Stable convention: dotted
    /// lower-case (e.g. <c>"approve.executory_documents"</c>).
    /// </summary>
    public required string Scope { get; set; }

    /// <summary>UTC instant at which the grant was issued.</summary>
    public DateTime GrantedAtUtc { get; set; }

    /// <summary>
    /// UTC instant at which the grant was revoked early, or <c>null</c> when the grant
    /// is either still active or has expired naturally at <see cref="ValidToUtc"/>.
    /// </summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>
    /// Free-form reason captured at revocation time, or <c>null</c> when the grant
    /// has not been revoked. Capped at 500 chars by the EF configuration.
    /// </summary>
    public string? RevokeReason { get; set; }
}
