using Cnas.Ps.Contracts.Search;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0167 — list-query input DTO for the Solicitant registry. Carried over the query
/// string of <c>GET /api/solicitants</c>; the controller maps it onto the service-layer
/// list call. All fields are optional — the budget guard will refuse the call when too
/// few filters narrow the registry.
/// </summary>
/// <param name="Q">
/// Free-text query (partial display-name match, diacritic-insensitive). When null /
/// empty the budget guard fires the corresponding hint.
/// </param>
/// <param name="CreatedFromUtc">
/// Inclusive lower bound on <c>Solicitant.CreatedAtUtc</c>. Hint: pair with
/// <see cref="CreatedToUtc"/> to keep the date range bounded.
/// </param>
/// <param name="CreatedToUtc">
/// Exclusive upper bound on <c>Solicitant.CreatedAtUtc</c>.
/// </param>
/// <param name="Page">1-based page number. Default 1.</param>
/// <param name="PageSize">Page size. Service clamps to [1, 200].</param>
public sealed record SolicitantListQueryInput(
    string? Q = null,
    DateTime? CreatedFromUtc = null,
    DateTime? CreatedToUtc = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>
/// R0167 — compact projection for the Solicitant registry list view. All ids are
/// Sqid-encoded per CLAUDE.md RULE 3.
/// </summary>
/// <param name="Id">Sqid-encoded internal id of the solicitant.</param>
/// <param name="DisplayName">Display name (full name or denumire).</param>
/// <param name="Kind">Classification — <c>NaturalPerson</c> or <c>LegalPerson</c>.</param>
/// <param name="CreatedAtUtc">Registration timestamp (UTC).</param>
public sealed record SolicitantListItem(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Kind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime CreatedAtUtc);

/// <summary>
/// R0163 / TOR UI 009 — request body for <c>POST /api/solicitants/search</c>. Combines the
/// legacy query-string list inputs with an optional <see cref="QbeFilterDto"/> envelope so
/// the UI can post a single payload containing both the canonical free-text + date range
/// filters and the open-ended QBE conditions.
/// </summary>
/// <param name="Q">
/// Free-text query (partial display-name match, diacritic-insensitive). Maps onto the
/// existing <c>Q</c> field on <see cref="SolicitantListQueryInput"/>.
/// </param>
/// <param name="CreatedFromUtc">Inclusive lower bound on creation timestamp.</param>
/// <param name="CreatedToUtc">Exclusive upper bound on creation timestamp.</param>
/// <param name="Page">1-based page number; default 1.</param>
/// <param name="PageSize">Page size; service clamps to [1, 200].</param>
/// <param name="Qbe">
/// Optional QBE envelope. <see langword="null"/> or empty means "no QBE narrowing" —
/// the call behaves identically to the legacy <c>GET</c> path.
/// </param>
public sealed record SolicitantSearchInput(
    string? Q = null,
    DateTime? CreatedFromUtc = null,
    DateTime? CreatedToUtc = null,
    int Page = 1,
    int PageSize = 20,
    QbeFilterDto? Qbe = null);

/// <summary>
/// R0525 / TOR CF 03.08 — response envelope for <c>POST /api/solicitants/search</c>.
/// Wraps the underlying page with a parallel <see cref="Suggestions"/> array carrying
/// any structured refinement prompts emitted by the search-suggestion service
/// (R0525); empty array when the result count was below the threshold or no
/// suggestion applies.
/// </summary>
/// <param name="Page">
/// Paged result returned by the service. Identical to the legacy
/// <see cref="PagedResult{SolicitantListItem}"/> shape so existing clients can ignore
/// <see cref="Suggestions"/>.
/// </param>
/// <param name="Suggestions">
/// Suggestion list — empty when no refinement is suggested. Stable across responses
/// (always present, never null) so the wire contract is shape-stable.
/// </param>
public sealed record SolicitantSearchOutput(
    PagedResult<SolicitantListItem> Page,
    IReadOnlyList<SearchSuggestionDto> Suggestions);
