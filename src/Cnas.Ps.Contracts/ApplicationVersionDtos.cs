namespace Cnas.Ps.Contracts;

/// <summary>
/// R0321 / R0224 / UI 008 — full output projection of a single
/// <c>ApplicationVersion</c> row, used by the single-version GET and by the save /
/// revert response. Includes the (potentially large) <see cref="FormDataJson"/>
/// payload — for list views use <see cref="ApplicationVersionSummaryDto"/> instead so
/// the response stays small.
/// </summary>
/// <param name="Id">Sqid-encoded id of the version row.</param>
/// <param name="ApplicationSqid">Sqid-encoded id of the owning <c>ServiceApplication</c>.</param>
/// <param name="VersionNumber">1-based monotonic version number for the application.</param>
/// <param name="FormDataJson">Serialised form payload at the moment of this save.</param>
/// <param name="CreatedByUserSqid">Sqid-encoded id of the user that saved this version.</param>
/// <param name="Source">
/// Stable enum value as a string (<c>Autosave</c> | <c>ManualSave</c> | <c>Submit</c> |
/// <c>Revert</c>). The string form keeps the API contract decoupled from the integer
/// persistence representation.
/// </param>
/// <param name="CreatedAtUtc">UTC timestamp when the snapshot was captured.</param>
/// <param name="Note">Optional free-form annotation supplied at save time.</param>
/// <param name="IsCurrent"><c>true</c> for the most-recent save; <c>false</c> for history.</param>
public sealed record ApplicationVersionOutputDto(
    string Id,
    string ApplicationSqid,
    int VersionNumber,
    string FormDataJson,
    string CreatedByUserSqid,
    string Source,
    DateTime CreatedAtUtc,
    string? Note,
    bool IsCurrent);

/// <summary>
/// R0321 / R0224 / UI 008 — listing projection of an <c>ApplicationVersion</c> row.
/// Omits <c>FormDataJson</c> to keep the list payload small — fetch a single version
/// with the GET endpoint when the payload is needed.
/// </summary>
/// <param name="Id">Sqid-encoded id of the version row.</param>
/// <param name="ApplicationSqid">Sqid-encoded id of the owning <c>ServiceApplication</c>.</param>
/// <param name="VersionNumber">1-based monotonic version number for the application.</param>
/// <param name="CreatedByUserSqid">Sqid-encoded id of the user that saved this version.</param>
/// <param name="Source">Stable enum value as a string.</param>
/// <param name="CreatedAtUtc">UTC timestamp when the snapshot was captured.</param>
/// <param name="Note">Optional free-form annotation supplied at save time.</param>
/// <param name="IsCurrent"><c>true</c> for the most-recent save; <c>false</c> for history.</param>
public sealed record ApplicationVersionSummaryDto(
    string Id,
    string ApplicationSqid,
    int VersionNumber,
    string CreatedByUserSqid,
    string Source,
    DateTime CreatedAtUtc,
    string? Note,
    bool IsCurrent);

/// <summary>
/// R0321 / R0224 / UI 008 — request body for <c>POST /api/applications/{sqid}/versions</c>.
/// The application Sqid lives on the route so it is NOT repeated here. The caller's
/// identity (resolved server-side from the authenticated principal) becomes the
/// <c>CreatedByUserId</c> — the input DTO deliberately omits the owner field to prevent
/// mass-assignment forgery (CLAUDE.md §2.4 / §5.5).
/// </summary>
/// <param name="FormDataJson">
/// Serialised form payload to snapshot. Validator enforces non-empty + syntactically
/// valid JSON + ≤ 500 KB string length.
/// </param>
/// <param name="Source">
/// Stable enum value as a string (<c>Autosave</c> | <c>ManualSave</c> | <c>Submit</c> |
/// <c>Revert</c>). Validator rejects unknown values.
/// </param>
/// <param name="Note">
/// Optional free-form annotation. Validator enforces ≤ 1000 chars when supplied.
/// </param>
public sealed record ApplicationVersionSaveDto(
    string FormDataJson,
    string Source,
    string? Note);
