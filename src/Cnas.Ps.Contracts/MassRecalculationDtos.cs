using System;
using System.Collections.Generic;
using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R1503 / TOR §3.7-D — Mass-recalculation engine DTOs
// ────────────────────────────────────────────────────────────────────────────
// Amounts carry the Confidential label (personal-finance figures).
// IDNP hashes carry the Internal label (hash, not PII).
// Status / mode / kind strings carry the Public label (state tokens).
// Contracts MUST NOT <see cref="..."/> Core — Contracts→Core is forbidden by
// LayerBoundaryTests.Contracts_HasNoOutboundDependencies.

/// <summary>
/// R1503 / TOR §3.7-D — outbound projection of a <c>LegalChangeEvent</c>.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id.</param>
/// <param name="Code">Stable external code (<c>^[A-Z][A-Z0-9_.]{1,63}$</c>).</param>
/// <param name="Title">Operator-facing title.</param>
/// <param name="Description">Optional rationale.</param>
/// <param name="EffectiveFrom">First month for which the new rule applies (UTC date).</param>
/// <param name="Scope">Stable enum-name of the coarse scope (e.g. <c>Pension</c>, <c>All</c>).</param>
/// <param name="BenefitTypesInScope">Snapshot of the benefit-kind enum-names in scope.</param>
/// <param name="ChangePayloadJson">Opaque JSON describing the change set (no PII).</param>
/// <param name="Status">Stable enum-name of the lifecycle status.</param>
/// <param name="RegisteredAt">UTC timestamp the row was created.</param>
/// <param name="CancellationReason">Operator rationale when the event was cancelled.</param>
public sealed record LegalChangeEventDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Code,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Scope,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    IReadOnlyList<string> BenefitTypesInScope,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ChangePayloadJson,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime RegisteredAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? CancellationReason);

/// <summary>
/// R1503 / TOR §3.7-D — input envelope for
/// <c>ILegalChangeEventService.RegisterAsync</c>.
/// </summary>
/// <param name="Code">Optional stable external code; auto-generated as <c>LCE-{year}-{seq:000000}</c> when null.</param>
/// <param name="Title">Operator-facing title (3..256 chars).</param>
/// <param name="Description">Optional rationale (≤ 2000 chars).</param>
/// <param name="EffectiveFrom">First month for which the new rule applies (UTC date).</param>
/// <param name="Scope">Stable enum-name of the coarse scope (e.g. <c>Pension</c>).</param>
/// <param name="BenefitTypesInScope">
/// Explicit benefit-kind enum-names in scope (≤ 50 entries). Ignored when <paramref name="Scope"/> is <c>All</c>:
/// the service snapshots every known benefit kind onto the event.
/// </param>
/// <param name="ChangePayloadJson">Optional opaque JSON describing the change (≤ 16384 chars, must parse as JSON).</param>
public sealed record LegalChangeEventRegisterInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Code,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Scope,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    IReadOnlyList<string> BenefitTypesInScope,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ChangePayloadJson);

/// <summary>
/// R1503 / TOR §3.7-D — modify envelope for <c>ILegalChangeEventService.ModifyAsync</c>.
/// Each non-null property is treated as a patch field. <paramref name="ChangeReason"/>
/// is mandatory and 3..500 chars.
/// </summary>
/// <param name="Title">New title (3..256 chars) when supplied.</param>
/// <param name="Description">New description (≤ 2000 chars) when supplied.</param>
/// <param name="EffectiveFrom">New effective-from date when supplied.</param>
/// <param name="Scope">New scope when supplied.</param>
/// <param name="BenefitTypesInScope">New explicit list when supplied.</param>
/// <param name="ChangePayloadJson">New opaque JSON when supplied.</param>
/// <param name="ChangeReason">Mandatory rationale for the modification (3..500 chars).</param>
public sealed record LegalChangeEventModifyInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Description,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly? EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Scope,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    IReadOnlyList<string>? BenefitTypesInScope,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ChangePayloadJson,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string ChangeReason);

/// <summary>
/// R1503 / TOR §3.7-D — single-field reason envelope used by the cancel
/// endpoint on the legal-change-event service.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record LegalChangeEventReasonInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);

/// <summary>
/// R1503 / TOR §3.7-D — filter envelope for the legal-change-events list endpoint.
/// </summary>
/// <param name="Status">Optional stable enum-name filter; null returns every status.</param>
/// <param name="Scope">Optional stable enum-name scope filter; null returns every scope.</param>
/// <param name="EffectiveFromAfter">Optional lower-bound on the effective-from date (inclusive).</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..100.</param>
public sealed record LegalChangeEventFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Scope,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    DateOnly? EffectiveFromAfter,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 50);

/// <summary>
/// R1503 / TOR §3.7-D — paged response envelope for the legal-change-events list endpoint.
/// </summary>
/// <param name="Items">Items page.</param>
/// <param name="Total">Total matching rows across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record LegalChangeEventPageDto(
    IReadOnlyList<LegalChangeEventDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);

/// <summary>
/// R1503 / TOR §3.7-D — outbound projection of a <c>RecalculationRun</c>.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the run.</param>
/// <param name="LegalChangeSqid">Sqid-encoded id of the parent legal-change event.</param>
/// <param name="TriggerKind">Stable enum-name of the trigger origin (<c>Scheduled</c> / <c>Manual</c>).</param>
/// <param name="Mode">Stable enum-name of the execution mode (<c>DryRun</c> / <c>Apply</c>).</param>
/// <param name="Status">Stable enum-name of the lifecycle status (<c>Running</c> / <c>Completed</c> / <c>Failed</c>).</param>
/// <param name="StartedAt">UTC timestamp the run started.</param>
/// <param name="CompletedAt">UTC timestamp the run completed; null while in flight.</param>
/// <param name="TotalDecisionsScanned">Total decisions scanned across every strategy invocation.</param>
/// <param name="TotalDecisionsRecalculated">Total decisions for which the strategy produced a Computed row.</param>
/// <param name="TotalSkipped">Total decisions tagged Skipped.</param>
/// <param name="TotalFailed">Total decisions tagged Failed.</param>
/// <param name="TotalDeltaMdl">Net MDL delta across every result row.</param>
/// <param name="FailureReason">Operator-facing reason populated when status is Failed.</param>
public sealed record RecalculationRunDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string LegalChangeSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string TriggerKind,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Mode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime StartedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? CompletedAt,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long TotalDecisionsScanned,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long TotalDecisionsRecalculated,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long TotalSkipped,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long TotalFailed,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalDeltaMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FailureReason);

/// <summary>
/// R1503 / TOR §3.7-D — outbound projection of a <c>RecalculationDecisionResult</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="BenefitDecisionId"/> is raw.</b> The mass-recalculation
/// admin surface is an INTERNAL ops dashboard. The decision aggregate may
/// not exist as a first-class entity in this build — the raw bigint is the
/// only stable forensic pointer. Sensitivity is <c>Internal</c> because the
/// row id itself is not PII. Mirrors the iter-76
/// <c>IntegrityCheckFindingDto.AggregateRowId</c> documented exception to
/// CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>IDNP hash, not raw IDNP.</b> Beneficiary identification is via the
/// HMAC IDNP hash so the row can be located without leaking PII.
/// </para>
/// </remarks>
/// <param name="Id">Sqid-encoded surrogate id of the result row.</param>
/// <param name="RunSqid">Sqid-encoded id of the parent run.</param>
/// <param name="BenefitDecisionId">Raw bigint PK of the underlying decision row (ops-only).</param>
/// <param name="BenefitType">Stable enum-name of the affected benefit kind.</param>
/// <param name="BeneficiaryIdnpHash">HMAC IDNP hash (base64, 44 chars).</param>
/// <param name="OldAmountMdl">Amount payable under the OLD rules in MDL.</param>
/// <param name="NewAmountMdl">Amount payable under the NEW rules in MDL.</param>
/// <param name="DeltaMdl"><c>NewAmountMdl - OldAmountMdl</c>.</param>
/// <param name="Status">Stable enum-name of the per-decision status.</param>
/// <param name="Reason">Operator-facing reason populated when status is non-Computed.</param>
/// <param name="AppliedAt">UTC timestamp the row moved to Applied; null otherwise.</param>
public sealed record RecalculationDecisionResultDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string RunSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    long BenefitDecisionId,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string BenefitType,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BeneficiaryIdnpHash,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal OldAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal NewAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal DeltaMdl,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Reason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? AppliedAt);

/// <summary>
/// R1503 / TOR §3.7-D — run summary plus the paged decision-results list.
/// Returned by the per-run detail endpoint.
/// </summary>
/// <param name="Run">Run summary header.</param>
/// <param name="Items">Per-decision result rows for the requested page.</param>
/// <param name="Total">Total matching result rows across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record RecalculationRunDetailsDto(
    RecalculationRunDto Run,
    IReadOnlyList<RecalculationDecisionResultDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);

/// <summary>
/// R1503 / TOR §3.7-D — filter envelope for the per-run decision-results query.
/// </summary>
/// <param name="Status">Optional stable enum-name status filter; null returns every status.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..200.</param>
public sealed record RecalculationResultFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 50);

/// <summary>
/// R1503 / TOR §3.7-D — filter envelope for the recalculation-runs list endpoint.
/// </summary>
/// <param name="Mode">Optional stable enum-name mode filter; null returns every mode.</param>
/// <param name="Status">Optional stable enum-name status filter; null returns every status.</param>
/// <param name="LegalChangeSqid">Optional parent legal-change-event Sqid; null returns runs for every event.</param>
/// <param name="Skip">Page offset; ≥ 0.</param>
/// <param name="Take">Page size; 1..100.</param>
public sealed record RecalculationRunFilterDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Mode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? LegalChangeSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip = 0,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take = 50);

/// <summary>
/// R1503 / TOR §3.7-D — paged response envelope for the recalculation-runs list endpoint.
/// </summary>
/// <param name="Items">Items page.</param>
/// <param name="Total">Total matching runs across all pages.</param>
/// <param name="Skip">Echoed page offset.</param>
/// <param name="Take">Echoed page size.</param>
public sealed record RecalculationRunPageDto(
    IReadOnlyList<RecalculationRunDto> Items,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Total,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Take);

/// <summary>
/// R1503 / TOR §3.7-D — single-field reason envelope for the
/// reject-result endpoint.
/// </summary>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
public sealed record RecalculationResultRejectInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason);
