using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts.Interop;

// ────────────────────────────────────────────────────────────────────────────
// R0634 / TOR CF 14.12 / Annex 4 — DTOs for the B2B interop surface exposed
// to other MGov systems (RSP, MoFin, IPS, ...). Every DTO carries a forensic
// IDNP-hash prefix (first 8 hex characters of the deterministic IDNP hash)
// in place of the raw IDNP so audit-pipeline forensics can correlate across
// rows without the surface ever echoing PII.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0634 / Annex 4 / CF 14.12 — request envelope for the interop endpoints
/// that take a single IDNP lookup key
/// (<c>GetInsuredPersonStatus</c>,
/// <c>GetBenefitsList</c>,
/// <c>GetPersonalAccountSnapshot</c>). The IDNP travels in the POST body —
/// never as a URL segment or query string — so it cannot leak via reverse
/// proxy access logs (one of the explicit anti-patterns called out in the
/// TOR security section on PII handling).
/// </summary>
/// <param name="Idnp">
/// Moldovan personal numeric code (13 digits, mod-10 checksum). Validated at
/// the service boundary; malformed values surface as
/// <c>INVALID_IDNP</c>. Never echoed back in any response shape.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record InteropIdnpRequestDto(string Idnp);

/// <summary>
/// R0634 / Annex 4 / CF 14.12 — request envelope for
/// <c>GetContributionHistory</c>. Carries the IDNP plus an inclusive
/// month-window. The window is capped at 60 months by the validator
/// (<c>InteropContributionHistoryRequestValidator.MaxWindowMonths</c>) to keep
/// inter-system pulls bounded — a typical caller (RSP, MoFin) needs at most
/// the rolling 5-year contribution stagiu.
/// </summary>
/// <param name="Idnp">
/// Same shape and discipline as
/// <see cref="InteropIdnpRequestDto.Idnp"/>.
/// </param>
/// <param name="FromMonth">
/// Inclusive lower bound of the contribution-month window. The day component
/// is ignored — only year + month are consulted because contribution entries
/// live on monthly buckets.
/// </param>
/// <param name="ToMonth">
/// Inclusive upper bound of the contribution-month window. Must be on or
/// after <paramref name="FromMonth"/>; the spanned width must not exceed
/// <c>InteropContributionHistoryRequestValidator.MaxWindowMonths</c>.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record InteropContributionHistoryRequestDto(
    string Idnp,
    DateOnly FromMonth,
    DateOnly ToMonth);

/// <summary>
/// R0634 / Annex 4 / CF 14.12 / <c>GetInsuredPersonStatus</c> — response
/// shape: whether the person is registered with CNAS at all, a Sqid handle
/// for the citizen's personal-account aggregate when one exists, plus a
/// count of active benefits as a quick sanity-check field.
/// </summary>
/// <remarks>
/// <para>
/// <b>Soft 404 by design.</b> Unlike the other three ops in this batch, this
/// endpoint returns 200 with <see cref="IsRegistered"/> = <c>false</c> for
/// unknown IDNPs. RSP and MoFin commonly probe an IDNP to find out whether
/// CNAS has any record at all; surfacing it as a 404 would force every
/// caller to wrap the call in <c>try/catch</c>. The interop audit row is
/// emitted on both branches so forensics can correlate either outcome.
/// </para>
/// </remarks>
/// <param name="IdnpHashPrefix">
/// First 8 hex characters of the deterministic IDNP HMAC-SHA256 hash. Used
/// by downstream audit systems to correlate "this caller asked about
/// IDNP-X" across rows without ever revealing the IDNP itself. Always
/// exactly 8 hex characters (<c>0-9a-f</c>).
/// </param>
/// <param name="IsRegistered">
/// <c>true</c> when an active <c>Solicitant</c> exists with a matching IDNP
/// hash; <c>false</c> otherwise (unknown IDNP, soft-deleted Solicitant).
/// </param>
/// <param name="AccountCode">
/// Stable opaque personal-account code (typically <c>"PA-XXXX"</c>) when a
/// <c>PersonalAccount</c> aggregate exists for the resolved Solicitant;
/// <c>null</c> when the citizen has no personal account on file. The shape
/// matches the placeholder code synthesized by the R0513 anonymous service.
/// </param>
/// <param name="ActiveBenefitsCount">
/// Distinct count of <c>BenefitType</c> values that have at least one active
/// <c>BenefitPayment</c> row attributed to the resolved Solicitant. Zero
/// when no benefits are on file (or when <see cref="IsRegistered"/> is
/// <c>false</c>).
/// </param>
/// <param name="AsOfUtc">
/// Server timestamp (UTC) at which the snapshot was assembled. Carried so
/// the caller can record an unambiguous as-of anchor in their own ledger.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record InsuredPersonStatusDto(
    string IdnpHashPrefix,
    bool IsRegistered,
    string? AccountCode,
    int ActiveBenefitsCount,
    DateTime AsOfUtc);

/// <summary>
/// R0634 / Annex 4 / CF 14.12 / <c>GetContributionHistory</c> — response
/// shape: a flat list of contribution-month rows inside the requested
/// window plus two summary fields (total amount and count of distinct
/// months). Each row is per-source — multiple sources covering the same
/// (Year, Month) bucket appear as separate rows so the caller can audit
/// the provenance of the totals.
/// </summary>
/// <param name="IdnpHashPrefix">
/// First 8 hex characters of the deterministic IDNP hash. Same discipline
/// as <see cref="InsuredPersonStatusDto.IdnpHashPrefix"/>.
/// </param>
/// <param name="Months">
/// Contribution rows inside the requested window, sorted by
/// (<see cref="ContributionMonthEntryDto.Year"/> ASC,
/// <see cref="ContributionMonthEntryDto.Month"/> ASC). May be empty when no
/// rows fall in the window.
/// </param>
/// <param name="TotalContributionsInWindow">
/// Sum of <see cref="ContributionMonthEntryDto.Paid"/> across every row in
/// <see cref="Months"/>. Currency is implicitly MDL (matches the underlying
/// <c>PersonalAccountEntry</c> column).
/// </param>
/// <param name="MonthsInWindow">
/// Count of distinct (<c>Year</c>, <c>Month</c>) buckets covered by
/// <see cref="Months"/>. May be less than <c>Months.Count</c> when multiple
/// sources contribute to the same bucket.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record ContributionHistoryDto(
    string IdnpHashPrefix,
    IReadOnlyList<ContributionMonthEntryDto> Months,
    decimal TotalContributionsInWindow,
    int MonthsInWindow);

/// <summary>
/// R0634 / Annex 4 / CF 14.12 — one contribution-month row inside
/// <see cref="ContributionHistoryDto.Months"/>. Carries the bucket
/// (<see cref="Year"/>, <see cref="Month"/>) plus the gross-base and the
/// actually-paid amount and the source tag that originated the row.
/// </summary>
/// <param name="Year">Calendar year (Gregorian) of the contribution bucket.</param>
/// <param name="Month">Calendar month (1..12) of the contribution bucket.</param>
/// <param name="Base">
/// Gross income subject to contribution for this month (MDL). Mirrors
/// <c>PersonalAccountEntry.ContributionBaseAmount</c>.
/// </param>
/// <param name="Paid">
/// Actual contribution paid for this month (MDL). Mirrors
/// <c>PersonalAccountEntry.ContributionPaidAmount</c>.
/// </param>
/// <param name="Source">
/// Stable source-code string (<c>"EMPLOYER_REPORT"</c>, <c>"DEC100"</c>,
/// <c>"INDIVIDUAL_PAYMENT"</c>) identifying the system/process that
/// produced the row. Mirrors <c>PersonalAccountEntry.SourceCode</c>.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record ContributionMonthEntryDto(
    int Year,
    int Month,
    decimal Base,
    decimal Paid,
    string Source);

/// <summary>
/// R0634 / Annex 4 / CF 14.12 / <c>GetBenefitsList</c> — response shape: one
/// row per benefit type the citizen has ever been paid (active or
/// historical). Grouping by <c>BenefitType</c> collapses the per-month
/// <c>BenefitPayment</c> ledger into a per-type summary suitable for "what
/// benefits is this person entitled to right now" probes from RSP / SIVE /
/// SIAAS.
/// </summary>
/// <param name="IdnpHashPrefix">
/// First 8 hex characters of the deterministic IDNP hash. Same discipline
/// as <see cref="InsuredPersonStatusDto.IdnpHashPrefix"/>.
/// </param>
/// <param name="Benefits">
/// One entry per distinct <c>BenefitType</c> the Solicitant has at least
/// one payment row for, sorted by <see cref="BenefitEntryDto.Type"/>
/// ascending. Empty when no payments are on file.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record BenefitsListDto(
    string IdnpHashPrefix,
    IReadOnlyList<BenefitEntryDto> Benefits);

/// <summary>
/// R0634 / Annex 4 / CF 14.12 — one row inside
/// <see cref="BenefitsListDto.Benefits"/>: per-type aggregate of the
/// citizen's payment ledger. The <see cref="Type"/> field is the stable
/// enum-name string (<c>"OldAgePension"</c>, <c>"ChildAllowance"</c>, ...)
/// so the caller can switch on a self-describing label without owning the
/// numeric mapping.
/// </summary>
/// <param name="Type">
/// Stable enum-name string of the <c>BenefitType</c>. Classified
/// <see cref="SensitivityLabel.Internal"/> because the benefit-type name
/// alone (without the IDNP) is not PII — the surrounding DTO carries the
/// Confidential floor for the actual personal context.
/// </param>
/// <param name="FirstPaymentMonth">
/// Earliest <c>PaymentMonth</c> across all rows of this type for the
/// Solicitant. <c>null</c> only on the historical edge case where the
/// payment rows have no <c>PaymentMonth</c> populated; in practice always
/// populated for non-empty groups.
/// </param>
/// <param name="LastPaymentMonth">
/// Latest <c>PaymentMonth</c> across all rows of this type. Pair with
/// <see cref="FirstPaymentMonth"/> to derive the active period span.
/// </param>
/// <param name="TotalPaymentsCount">
/// Number of <c>BenefitPayment</c> rows of this type for the Solicitant.
/// Includes every lifecycle state (Scheduled, Issued, Paid, Returned,
/// Cancelled) so the caller sees the full footprint.
/// </param>
public sealed record BenefitEntryDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Type,
    DateOnly? FirstPaymentMonth,
    DateOnly? LastPaymentMonth,
    int TotalPaymentsCount);

/// <summary>
/// R0634 / Annex 4 / CF 14.12 / <c>GetPersonalAccountSnapshot</c> — response
/// shape: the cached lifetime totals from the citizen's
/// <c>PersonalAccount</c> aggregate. These are the projection caches
/// recomputed by the application layer whenever new contribution entries
/// land; consumers of this endpoint accept the eventually-consistent
/// nature documented on <c>PersonalAccount</c>.
/// </summary>
/// <param name="IdnpHashPrefix">
/// First 8 hex characters of the deterministic IDNP hash. Same discipline
/// as <see cref="InsuredPersonStatusDto.IdnpHashPrefix"/>.
/// </param>
/// <param name="AccountCode">
/// Stable opaque personal-account code (typically <c>"PA-XXXX"</c>).
/// Always populated when the response succeeds — the controller surfaces
/// 404 when no account is on file rather than returning a snapshot with
/// nulls.
/// </param>
/// <param name="LifetimeContributions">
/// Sum of every <c>PersonalAccountEntry.ContributionPaidAmount</c> rolled
/// up across the account's lifetime (MDL). Mirrors
/// <c>PersonalAccount.LifetimeContributions</c>.
/// </param>
/// <param name="LifetimeMonths">
/// Count of distinct (<c>Year</c>, <c>Month</c>) buckets that hold at least
/// one contribution entry. Mirrors <c>PersonalAccount.LifetimeMonths</c>.
/// </param>
/// <param name="AsOfUtc">
/// Server timestamp (UTC) at which the snapshot was assembled.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record PersonalAccountSnapshotDto(
    string IdnpHashPrefix,
    string AccountCode,
    decimal LifetimeContributions,
    int LifetimeMonths,
    DateTime AsOfUtc);
