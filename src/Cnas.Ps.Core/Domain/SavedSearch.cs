namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0165 / CF 03.06 — a user-saved registry search. Each row captures the registry code, a
/// user-supplied friendly name, and an opaque JSON filter specification so the same query
/// can be replayed later. Owners may flip <see cref="IsShared"/> to publish the query to
/// every authenticated CNAS staff member, enabling colleagues to reuse a curated filter
/// without recreating it field-by-field.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership and sharing.</b> The <see cref="OwnerUserId"/> is the internal primary key
/// of the <c>UserProfile</c> that created the row. Ownership is unilateral — only the owner
/// may update, soft-delete, or change the <see cref="IsShared"/> flag. Non-owners receive
/// READ access exclusively to rows that the owner has explicitly published
/// (<c>IsShared = true</c>); they cannot mutate shared rows in any way. There is no
/// per-team ACL in this batch — that scoping is a R0056 ABAC concern (see batch decision
/// notes for R0165) and would layer on top of this entity without schema changes.
/// </para>
/// <para>
/// <b>Filter payload opacity.</b> <see cref="FilterJson"/> is treated as an opaque blob by
/// every service-layer boundary: the saved-search service round-trips it verbatim, the
/// search service interprets the document, and the audit subsystem records only the
/// registry + name (never the JSON body) so PII inadvertently captured inside a filter
/// term cannot leak through the audit trail. The PII redactor (R0185) provides additional
/// defense-in-depth against accidental leaks; the design contract is that callers don't
/// stash IDNP / IDNO / account numbers in filter terms in the first place.
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b> A unique composite index on
/// <c>(OwnerUserId, Registry, Name)</c> (declared in <c>SavedSearchConfiguration</c>) makes
/// a same-name save on the same registry deterministic: the service treats a duplicate
/// create as idempotent (returns the existing row's Sqid id), and updates are routed by
/// Sqid id so name collisions can never silently rename a row. The triple is the natural
/// key callers rely on when re-saving a search under a familiar label.
/// </para>
/// <para>
/// <b>Soft-delete contract.</b> Inherits <see cref="AuditableEntity.IsActive"/> from
/// <see cref="AuditableEntity"/>. Deletes flip <see cref="AuditableEntity.IsActive"/> to
/// <c>false</c>; the row remains queryable for audit forensics but no longer surfaces
/// through <c>ListAsync</c> / <c>GetAsync</c>. Hard delete is reserved for GDPR
/// erasure flows that operate at the user-profile level (R0148) and is out of scope
/// here.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> The numeric <see cref="AuditableEntity.Id"/> never leaves the
/// system. The marker <see cref="IExternalId"/> is applied because the <c>SavedSearchItem</c>
/// output DTO surfaces the row's Sqid as the public identifier — CLAUDE.md RULE 3 / ARH 027.
/// </para>
/// </remarks>
public sealed class SavedSearch : AuditableEntity, IExternalId
{
    /// <summary>
    /// Internal <c>UserProfile.Id</c> of the row's owner — the only actor permitted to
    /// modify the row regardless of <see cref="IsShared"/>. Non-owners may only read rows
    /// that the owner has flagged <c>IsShared = true</c>. Captured at create time and
    /// never reassigned (transferring a saved search between users would require a separate
    /// admin endpoint, intentionally not provided in this batch).
    /// </summary>
    public long OwnerUserId { get; set; }

    /// <summary>
    /// Stable registry code identifying which grid this search applies to (e.g.
    /// <c>Contributors</c>, <c>Insured</c>, <c>Applications</c>, <c>Notifications</c>).
    /// Forwarded verbatim to <c>IDataSearchService.SearchAsync</c> when the saved row is
    /// replayed — the saved-search service does not validate the code against any
    /// registry whitelist because the set of registries is open-ended (each new feature
    /// introduces its own registry view), and an invalid code merely produces an empty
    /// result on replay.
    /// </summary>
    public required string Registry { get; set; }

    /// <summary>
    /// User-supplied friendly name (e.g. "Active applications — Chișinău", "My RSP
    /// imports"). Capped at 128 chars by the EF mapping; the service layer enforces the
    /// same cap before persisting so over-long names surface as
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.ValidationFailed"/> rather than as a
    /// DB-side <c>DbUpdateException</c>.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// JSON-encoded filter specification — the persistence-friendly mirror of the runtime
    /// <c>SearchRequest</c> filter shape. Treated as an opaque blob at this layer (see
    /// class-level remarks). Capped at 8192 bytes by the service-layer validator; the DB
    /// column is <c>text</c> with no hard cap so the service can be relaxed later
    /// without a migration if a richer filter shape lands.
    /// </summary>
    public required string FilterJson { get; set; }

    /// <summary>
    /// When <c>true</c>, every authenticated CNAS staff member can READ this row. The
    /// owner remains the sole mutator. Default is <c>false</c> — saving is private until
    /// the owner explicitly publishes. The flag is unilateral (no per-recipient ACL); a
    /// future ABAC pass (R0056) may layer per-team scoping on top by intersecting this
    /// flag with the caller's team membership.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Legacy coupling with <see cref="SharingScope"/>.</b> This flag was the original
    /// R0165 binary sharing toggle. As of R0524 the canonical sharing field is
    /// <see cref="SharingScope"/>; <see cref="IsShared"/> is kept synchronised by the
    /// service layer ( <c>true</c> when <see cref="SharingScope"/> is
    /// <see cref="SavedSearchSharingScope.Shared"/>, <c>false</c> for
    /// <see cref="SavedSearchSharingScope.Private"/> and
    /// <see cref="SavedSearchSharingScope.Group"/>) so existing readers that pre-date
    /// R0524 continue to work without code change. New code should consume
    /// <see cref="SharingScope"/> instead.
    /// </para>
    /// </remarks>
    public bool IsShared { get; set; }

    /// <summary>
    /// R0524 / TOR CF 03.06 — granular sharing scope. Default
    /// <see cref="SavedSearchSharingScope.Private"/> matches the pre-R0524 behaviour
    /// (visible only to the owner). The owner may upgrade to
    /// <see cref="SavedSearchSharingScope.Shared"/> (visible to everyone with
    /// <c>Search.View</c>) or <see cref="SavedSearchSharingScope.Group"/> (visible to
    /// every member of the group identified by <see cref="SharedWithGroupCode"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Companion field invariant.</b> When <see cref="SharingScope"/> is
    /// <see cref="SavedSearchSharingScope.Group"/> the <see cref="SharedWithGroupCode"/>
    /// column MUST be populated with a non-empty kebab/dotted identifier matching
    /// <c>^[a-z][a-z0-9._-]{1,63}$</c>; for the other two scopes it MUST be <c>null</c>.
    /// The service layer enforces both halves of the invariant on every share / update
    /// path before persisting; the entity itself does not encode the constraint because
    /// EF Core has no first-class cross-column check helper.
    /// </para>
    /// </remarks>
    public SavedSearchSharingScope SharingScope { get; set; } = SavedSearchSharingScope.Private;

    /// <summary>
    /// R0524 / TOR CF 03.06 — group code (lowercase kebab/dotted identifier, e.g.
    /// <c>pensions.examiners</c>) the saved search is shared with. Populated only when
    /// <see cref="SharingScope"/> is <see cref="SavedSearchSharingScope.Group"/>; in
    /// every other scope this column MUST be <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The code is matched, case-sensitively, against the caller's
    /// <c>UserProfile.Groups</c> list at access time. Groups are NOT validated against a
    /// reference table at write time — the listing query is naturally fail-closed
    /// (callers whose group set doesn't intersect simply receive no row), so a typo in
    /// the group code degrades to "nobody can see this" rather than to a security
    /// exposure. The format regex enforced by the service-layer validator keeps the
    /// vocabulary tight without depending on a registry.
    /// </para>
    /// </remarks>
    public string? SharedWithGroupCode { get; set; }
}
