using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0671 continuation — request body for <c>POST /api/decisions/search</c>. The
/// decision-registry list shows the lifecycle of dossier-level decisions (open →
/// approved / rejected) projected from the <c>Dossier</c> +
/// <c>ServiceApplication</c> aggregate; no standalone <c>Decision</c> entity
/// exists today (the UC10 workflow records its verdict on the parent application's
/// status field).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why list dossiers.</b> The "decisions registry" surface from TOR §2.3 #7 maps
/// onto the dossier rows — a dossier is opened when a decision draft is first
/// drafted and is closed when the decider records a final verdict. Drafted = dossier
/// CreatedAtUtc; finalised = dossier ClosedAtUtc; status = parent application.Status
/// projected through enum string.
/// </para>
/// <para>
/// <b>Paging cap.</b> Mirrors <see cref="DocumentsListInput"/> — server clamps
/// <see cref="Take"/> to 200; validator rejects values above the cap.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> The envelope carries no raw database identifiers; outbound
/// rows expose Sqid-encoded ids per CLAUDE.md RULE 3.
/// </para>
/// </remarks>
/// <param name="Filter">Optional QBE envelope; null treated as "no QBE filter".</param>
/// <param name="FromUtc">
/// Inclusive lower bound on the dossier's <c>CreatedAtUtc</c> (i.e. when the
/// decision was drafted). When both <see cref="FromUtc"/> and <see cref="ToUtc"/>
/// are supplied the validator enforces <see cref="FromUtc"/> ≤ <see cref="ToUtc"/>.
/// </param>
/// <param name="ToUtc">
/// Exclusive upper bound on the dossier's <c>CreatedAtUtc</c>.
/// </param>
/// <param name="Skip">Zero-based row offset; validator rejects negatives.</param>
/// <param name="Take">Maximum rows to return; server cap = 200.</param>
public sealed record DecisionsListInput(
    QbeFilterDto? Filter = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// R0671 continuation — paged response envelope for <c>POST /api/decisions/search</c>.
/// Mirrors the <see cref="DocumentsListPageDto"/> shape: row list + total count.
/// </summary>
/// <remarks>
/// Type-level sensitivity floor is <see cref="SensitivityLabel.Internal"/> — the
/// decision metadata exposes dossier numbers which are pseudo-identifiers when joined
/// against the application + solicitant graph.
/// </remarks>
/// <param name="Items">Materialised rows for the requested page.</param>
/// <param name="TotalCount">Total rows matching the filter (server-evaluated).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record DecisionsListPageDto(
    IReadOnlyList<DecisionListItemDto> Items,
    int TotalCount);

/// <summary>
/// R0671 continuation — single-row projection for the decision-registry list. All ids
/// are Sqid-encoded per CLAUDE.md RULE 3.
/// </summary>
/// <param name="Id">Sqid-encoded dossier primary key — the decision identifier.</param>
/// <param name="ServiceApplicationSqid">
/// Sqid-encoded parent <c>ServiceApplication</c> id.
/// </param>
/// <param name="Status">
/// Stable enum-name string for the parent application's <c>Status</c> at the time
/// the row was projected — e.g. <c>"PendingApproval"</c>, <c>"Approved"</c>,
/// <c>"Rejected"</c>.
/// </param>
/// <param name="DraftedAtUtc">UTC timestamp the dossier was opened (decision drafted).</param>
/// <param name="FinalisedAtUtc">
/// UTC timestamp the dossier was closed (decision finalised). <see langword="null"/>
/// while the decision is still in flight.
/// </param>
/// <param name="DraftedByUserSqid">
/// Sqid-encoded user id of the operator who drafted the decision — mapped from the
/// dossier's <c>AssignedExaminerId</c>. <see langword="null"/> when the dossier has
/// no assigned examiner (auto-assigned flows / legacy rows).
/// </param>
/// <param name="DossierNumber">
/// Public dossier reference number printed on confirmations and decisions.
/// </param>
public sealed record DecisionListItemDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ServiceApplicationSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime DraftedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? FinalisedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? DraftedByUserSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DossierNumber);
