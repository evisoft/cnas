using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0671 continuation — admin back-fill request for the
/// <c>Solicitant.RegionCode</c> column. R0671 introduced the column NOT-backfilled
/// (NULL = "national scope" — visible to every caller); operators need a tool to
/// bulk-assign the column to existing rows after-the-fact. This input envelope
/// drives <c>POST /api/admin/access-scope/solicitants/backfill-region</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection contract.</b> Exactly one of <see cref="Filter"/> or
/// <see cref="ExplicitSolicitantSqids"/> MUST be present — both null is rejected
/// by the validator. When both are supplied the service treats the resulting row
/// set as the union of the two — every row matched by either path is updated.
/// </para>
/// <para>
/// <b>Hard cap.</b> The service refuses any call whose matched row set exceeds
/// 5000 (<c>BACKFILL_QUOTA_EXCEEDED</c>). The validator caps
/// <see cref="ExplicitSolicitantSqids"/> at 5000 entries belt-and-braces; the
/// runtime cap on the QBE path defends against an over-permissive filter.
/// </para>
/// <para>
/// <b>Sensitivity.</b> The region code is non-public ops metadata (Internal); the
/// Sqid list is also Internal because it identifies individual citizens.
/// </para>
/// </remarks>
/// <param name="RegionCode">Region allow-list value to assign — e.g. <c>"CHIS"</c>.</param>
/// <param name="Filter">Optional QBE filter selecting the row set; null = no QBE path.</param>
/// <param name="ExplicitSolicitantSqids">Optional list of Sqid-encoded ids; null = no explicit path.</param>
public sealed record AccessScopeSolicitantBackfillInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string RegionCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    QbeFilterDto? Filter = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string>? ExplicitSolicitantSqids = null);

/// <summary>
/// R0671 continuation — admin back-fill request for the
/// <c>ServiceApplication.SubdivisionCode</c> column. Mirrors
/// <see cref="AccessScopeSolicitantBackfillInputDto"/> 1:1 for the subdivision axis.
/// </summary>
/// <remarks>
/// <para>
/// <b>SubdivisionCode validation.</b> The service validates the supplied
/// <see cref="SubdivisionCode"/> against the active rows of
/// <c>CnasBranch.Code</c> (R0512). Unknown codes return
/// <c>BRANCH_NOT_FOUND</c> so a typo surfaces loudly rather than silently flagging
/// rows with a dead code.
/// </para>
/// </remarks>
/// <param name="SubdivisionCode">Active <c>CnasBranch.Code</c> value to assign.</param>
/// <param name="Filter">Optional QBE filter selecting the row set; null = no QBE path.</param>
/// <param name="ExplicitApplicationSqids">Optional list of Sqid-encoded ids; null = no explicit path.</param>
public sealed record AccessScopeApplicationBackfillInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SubdivisionCode,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    QbeFilterDto? Filter = null,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string>? ExplicitApplicationSqids = null);

/// <summary>
/// R0671 continuation — per-row failure carried back in
/// <see cref="AccessScopeBackfillResultDto.Failures"/>. One entry is emitted per
/// Sqid in the explicit list that did not resolve to an active row (e.g. the
/// Sqid is well-formed but the underlying row was soft-deleted).
/// </summary>
/// <param name="Sqid">Sqid as it appeared on the inbound request.</param>
/// <param name="ErrorCode">
/// Stable <c>ErrorCodes</c> value (e.g. <c>INVALID_ID</c>, <c>INVALID_SQID</c>).
/// </param>
/// <param name="Message">Human-readable description of the failure.</param>
public sealed record AccessScopeBackfillFailureDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ErrorCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Message);

/// <summary>
/// R0671 continuation — response envelope for the back-fill helper.
/// </summary>
/// <param name="RowsUpdated">Number of rows whose scoped column was actually changed.</param>
/// <param name="MatchedSqidCount">
/// Number of Sqids from <c>ExplicitSolicitantSqids</c> /
/// <c>ExplicitApplicationSqids</c> that resolved to an active row. Zero when the
/// caller used only the QBE filter path.
/// </param>
/// <param name="Failures">Per-row failures keyed by inbound Sqid; empty when none.</param>
public sealed record AccessScopeBackfillResultDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int RowsUpdated,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int MatchedSqidCount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<AccessScopeBackfillFailureDto> Failures);
