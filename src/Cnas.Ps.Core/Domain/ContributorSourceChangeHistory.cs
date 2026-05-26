namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0302 / TOR §2.1 — per-change history row capturing every mutation of the
/// <c>SourceSystem</c> attribution of a <see cref="Contributor"/>. Whenever the
/// authoritative origin of a contributor's data flips (e.g. a manually-registered
/// row is later reconciled against a freshly-arrived RSUD record, or vice-versa),
/// the writer logs one row here. The companion DB table is append-only and the
/// rows are never updated nor soft-deleted — see remarks for the audit-trail
/// semantics.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only contract.</b> Existing rows are immutable; corrections add a
/// new row rather than rewriting history. <see cref="AuditableEntity.IsActive"/>
/// is therefore left at its inherited <c>true</c> default and never flipped.
/// </para>
/// <para>
/// <b>String, not enum.</b> Source values are stored as free-form strings
/// (<c>"Manual"</c>, <c>"RSUD"</c>, <c>"SFS"</c>, future feeds) capped at 64
/// chars because the source vocabulary is open-ended — adding a new upstream
/// feed should not require an enum + migration. The application service validates
/// against the current allow-list; historical rows preserving a retired value
/// remain legible.
/// </para>
/// <para>
/// <b>No PII.</b> The row captures only the source attribution and a free-form
/// reason; the contributor's IDNO / name are NOT duplicated here — they live on
/// the parent <see cref="Contributor"/> row. This keeps the history table cheap
/// to expose to ops dashboards without breaching the no-PII invariant.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> The <see cref="AuditableEntity.Id"/> primary key is
/// exposed on the DTO as a Sqid string per CLAUDE.md RULE 3 — hence the
/// <see cref="IExternalId"/> marker.
/// </para>
/// </remarks>
public sealed class ContributorSourceChangeHistory : AuditableEntity, IExternalId
{
    /// <summary>FK to the affected <see cref="Contributor"/> row.</summary>
    public long ContributorId { get; set; }

    /// <summary>Navigation to the parent contributor.</summary>
    public Contributor? Contributor { get; set; }

    /// <summary>
    /// Prior source-system attribution. <c>null</c> on the FIRST history row written
    /// for a contributor (initial registration).
    /// </summary>
    public string? OldSourceSystem { get; set; }

    /// <summary>
    /// New source-system attribution recorded by this change. Required; ≤ 64 chars.
    /// Common values are <c>"Manual"</c>, <c>"RSUD"</c>, <c>"SFS"</c>, but the column
    /// is intentionally string-typed to accept future feeds without a migration.
    /// </summary>
    public required string NewSourceSystem { get; set; }

    /// <summary>
    /// UTC instant the change occurred. Distinct from
    /// <see cref="AuditableEntity.CreatedAtUtc"/> so the business event timestamp
    /// survives any future row-level re-bake.
    /// </summary>
    public DateTime ChangedAtUtc { get; set; }

    /// <summary>
    /// FK to the <see cref="UserProfile"/> primary id of the operator who recorded
    /// the change. <c>null</c> for system / background writers (e.g. nightly RSUD
    /// reconciliation job).
    /// </summary>
    public int? ChangedByUserId { get; set; }

    /// <summary>
    /// Free-form operator-supplied justification (≤ 500 chars). Optional — system
    /// writers may leave it null; operator-driven changes SHOULD populate it.
    /// </summary>
    public string? Reason { get; set; }
}
