namespace Cnas.Ps.Contracts;

/// <summary>
/// One row in the saved-search listing (R0165 / CF 03.06). Identifiers are Sqid-encoded
/// strings per CLAUDE.md RULE 3 — raw <see cref="long"/> primary keys are never published
/// at the API boundary so third parties cannot infer business intelligence from
/// sequential ids.
/// </summary>
/// <param name="Id">Sqid-encoded id of the saved-search row.</param>
/// <param name="Registry">Registry code the saved query applies to (e.g. <c>Contributors</c>).</param>
/// <param name="Name">User-supplied friendly name for the saved query.</param>
/// <param name="FilterJson">
/// Opaque JSON filter specification; round-tripped verbatim through the API. The shape
/// mirrors the filter half of <see cref="SearchRequest"/> but is intentionally not
/// strongly typed at this layer so the contract survives future filter-shape evolutions
/// without breaking persisted rows.
/// </param>
/// <param name="IsShared">
/// True when the owner has published the row to every authenticated CNAS staff member.
/// Owners may always read, update, and delete their own rows; non-owners see only rows
/// with <c>IsShared = true</c>.
/// </param>
/// <param name="OwnerUserId">
/// Sqid-encoded <c>UserProfile.Id</c> of the row's owner. Surfaced so the UI can render
/// "Saved by ..." labels and gate edit controls. The raw numeric id never crosses the
/// boundary.
/// </param>
/// <param name="SharingScope">
/// R0524 / TOR CF 03.06 — stable string name of the row's
/// <c>SavedSearchSharingScope</c>: <c>Private</c>, <c>Shared</c>, or <c>Group</c>. The
/// service emits the enum's <c>ToString()</c> name so the client UI can switch on a
/// self-describing label without owning the numeric mapping.
/// </param>
/// <param name="SharedWithGroupCode">
/// R0524 / TOR CF 03.06 — group code (lowercase kebab/dotted identifier) the row is
/// shared with. Populated only when <c>SharingScope</c> is <c>Group</c>; <c>null</c>
/// for <c>Private</c> and <c>Shared</c>.
/// </param>
public sealed record SavedSearchItem(
    string Id,
    string Registry,
    string Name,
    string FilterJson,
    bool IsShared,
    string OwnerUserId,
    string SharingScope,
    string? SharedWithGroupCode);

/// <summary>
/// Request body for <c>POST /api/saved-searches</c>. The caller's identity (resolved
/// server-side from the authenticated principal) becomes the owner — the input DTO
/// deliberately does NOT carry an owner field so a non-admin caller cannot forge a row
/// for someone else (mass-assignment protection per CLAUDE.md §2.4 / §5.5).
/// </summary>
/// <param name="Registry">Registry code the saved query targets.</param>
/// <param name="Name">User-supplied friendly name; service caps at 128 chars.</param>
/// <param name="FilterJson">Opaque JSON filter; service caps at 8192 bytes.</param>
/// <param name="IsShared">When <c>true</c>, every authenticated CNAS staff member can read the row.</param>
public sealed record SavedSearchCreateInput(
    string Registry,
    string Name,
    string FilterJson,
    bool IsShared);

/// <summary>
/// Request body for <c>PUT /api/saved-searches/{sqid}</c>. Updates the three mutable
/// fields of a saved search; <see cref="SavedSearchItem.Registry"/> is immutable after
/// create because re-pointing a saved query at a different registry would invalidate its
/// filter shape.
/// </summary>
/// <param name="Name">New friendly name; service caps at 128 chars.</param>
/// <param name="FilterJson">New opaque JSON filter; service caps at 8192 bytes.</param>
/// <param name="IsShared">Updated sharing flag.</param>
public sealed record SavedSearchUpdateInput(
    string Name,
    string FilterJson,
    bool IsShared);

/// <summary>
/// R0524 / TOR CF 03.06 — request body for
/// <c>POST /api/saved-searches/{sqid}/share</c>. Flips the sharing scope of the named
/// row. The caller MUST be the owner; non-owner callers receive
/// <c>ErrorCodes.Forbidden</c> at the service boundary.
/// </summary>
/// <param name="SharingScope">
/// Stable string name of the target <c>SavedSearchSharingScope</c>: one of
/// <c>Private</c>, <c>Shared</c>, or <c>Group</c>. Anything else surfaces as
/// <c>ErrorCodes.ValidationFailed</c> through the FluentValidation pipeline.
/// </param>
/// <param name="SharedWithGroupCode">
/// Lowercase kebab/dotted group identifier (e.g. <c>pensions.examiners</c>). MUST be
/// non-null when <see cref="SharingScope"/> is <c>Group</c> and MUST be <c>null</c>
/// for <c>Private</c> / <c>Shared</c>. The validator enforces both halves of the
/// invariant.
/// </param>
public sealed record SavedSearchShareInput(
    string SharingScope,
    string? SharedWithGroupCode);
