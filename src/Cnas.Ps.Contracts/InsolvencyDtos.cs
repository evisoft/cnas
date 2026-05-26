using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0830 / R0834 — Insolvency lifecycle registry (Annex 1 §8.1.4.5)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — output projection for one insolvency
/// lifecycle case as it leaves the system. Every id is Sqid-encoded per
/// CLAUDE.md RULE 3; status surfaces as a stable enum-name string.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the insolvency case row.</param>
/// <param name="ContributorSqid">Sqid-encoded id of the owning payer (Contributor).</param>
/// <param name="Status">
/// Stable enum-name representation of the
/// <c>Cnas.Ps.Core.Domain.InsolvencyCaseStatus</c> value (<c>Open</c>,
/// <c>Resolved</c>).
/// </param>
/// <param name="InsolvencyDate">Calendar date the contributor was declared insolvent.</param>
/// <param name="Reason">Operator-supplied rationale captured at open time.</param>
/// <param name="OpenedAtUtc">UTC instant the case was opened.</param>
/// <param name="ResolvedAtUtc">UTC instant the case was resolved, or <c>null</c> while open.</param>
/// <param name="Resolution">Resolution rationale, or <c>null</c> while open.</param>
public sealed record InsolvencyCaseDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly InsolvencyDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime OpenedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime? ResolvedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Resolution);

/// <summary>
/// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — input DTO for
/// <c>POST /api/insolvency/open</c>. The lifecycle service flips
/// <c>Contributor.IsInsolvent=true</c> AND inserts a new
/// <c>InsolvencyCase</c> row in one atomic save.
/// </summary>
/// <param name="ContributorSqid">Sqid-encoded payer id.</param>
/// <param name="Reason">Operator-supplied rationale (3..500 chars).</param>
/// <param name="InsolvencyDate">Effective insolvency date; must not be in the future.</param>
public sealed record InsolvencyOpenInputDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ContributorSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Reason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly InsolvencyDate);

/// <summary>
/// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — input DTO for
/// <c>POST /api/insolvency/{sqid}/resolve</c>. Marks the open case as resolved
/// and concurrently flips <c>Contributor.IsInsolvent</c> back to false.
/// </summary>
/// <param name="Resolution">Resolution rationale (3..500 chars).</param>
public sealed record InsolvencyResolveInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Resolution);

/// <summary>
/// R0834 / TOR Annex 1 §8.1.4.5 — one claim row lodged against an insolvency
/// case as it leaves the system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the claim row.</param>
/// <param name="InsolvencyCaseSqid">Sqid-encoded id of the parent case.</param>
/// <param name="Amount">Claim amount in <paramref name="Currency"/> units.</param>
/// <param name="Currency">ISO-4217 currency code (e.g. <c>"MDL"</c>).</param>
/// <param name="Description">Free-form claim description.</param>
/// <param name="IncurredOn">Calendar date the underlying obligation was incurred.</param>
public sealed record InsolvencyClaimDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string InsolvencyCaseSqid,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal Amount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Currency,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Description,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly IncurredOn);

/// <summary>
/// R0834 — input DTO for <c>POST /api/insolvency/{caseSqid}/claims</c>. The
/// service refuses claims against a case that is already
/// <c>InsolvencyCaseStatus.Resolved</c>.
/// </summary>
/// <param name="Amount">Claim amount (&gt; 0, ≤ 100_000_000).</param>
/// <param name="Currency">ISO-4217 currency code (3 uppercase chars).</param>
/// <param name="Description">Free-form claim description (3..1000 chars).</param>
/// <param name="IncurredOn">Calendar date the obligation was incurred; ≤ today.</param>
public sealed record InsolvencyClaimInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal Amount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Currency,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Description,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly IncurredOn);

/// <summary>
/// R0834 — one payment row received against an insolvency case as it leaves the
/// system.
/// </summary>
/// <param name="Id">Sqid-encoded surrogate id of the payment row.</param>
/// <param name="InsolvencyCaseSqid">Sqid-encoded id of the parent case.</param>
/// <param name="Amount">Payment amount (MDL).</param>
/// <param name="PaymentDate">Calendar date the payment was received.</param>
/// <param name="Reference">Optional external payment reference (e.g. court-distribution order).</param>
public sealed record InsolvencyPaymentDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string InsolvencyCaseSqid,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal Amount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PaymentDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Reference);

/// <summary>
/// R0834 — input DTO for <c>POST /api/insolvency/{caseSqid}/payments</c>.
/// </summary>
/// <param name="Amount">Payment amount (&gt; 0, ≤ 100_000_000).</param>
/// <param name="PaymentDate">Calendar date the payment was received; ≤ today.</param>
/// <param name="Reference">Optional external payment reference (≤ 64 chars).</param>
public sealed record InsolvencyPaymentInputDto(
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal Amount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PaymentDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? Reference = null);
