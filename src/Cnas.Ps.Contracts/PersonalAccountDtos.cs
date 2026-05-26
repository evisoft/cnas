using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0516 — Personal-account extract (authenticated self-service)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0516 / TOR CF 02.04 — citizen-facing extract from the CNAS personal
/// account. Aggregates all contribution entries on file per calendar year,
/// surfaces the grand total + count of months, and carries the opaque
/// account code + Sqid-encoded Solicitant id for cross-reference. The
/// endpoint is authenticated; the caller's Solicitant is resolved
/// server-side from <c>ICallerContext</c> (or supplied explicitly for the
/// admin / utilizator-autorizat surface gated by <c>PersonalAccount.ReadAny</c>).
/// </summary>
/// <param name="AccountCodeSqid">
/// Stable, opaque CNAS personal-account identifier (e.g. <c>"PA-XXXX"</c>).
/// Sourced directly from <c>PersonalAccount.AccountCode</c>; the property name
/// preserves the "Sqid" suffix because the value occupies the same external
/// id slot as a Sqid-encoded surrogate would (CLAUDE.md RULE 3 boundary).
/// </param>
/// <param name="SolicitantSqid">
/// Sqid-encoded surrogate id of the owning <c>Solicitant</c>. Included so the
/// admin surface can cross-link to the Solicitant detail page; the citizen
/// self-service surface treats it as opaque.
/// </param>
/// <param name="Years">
/// Per-year aggregations in descending chronological order (newest first).
/// May be empty when the account has no entries yet — see the "empty
/// account" service test for the expected payload shape.
/// </param>
/// <param name="GrandTotalContributions">
/// Sum of <c>PersonalAccountEntry.ContributionPaidAmount</c> across every
/// entry on the account (MDL). Always non-negative.
/// </param>
/// <param name="GrandTotalMonths">
/// Count of distinct months with at least one contribution entry. Equal to
/// the sum of <see cref="PersonalAccountYearDto.Months"/> across
/// <see cref="Years"/>.
/// </param>
/// <param name="GeneratedAtUtc">
/// Server timestamp (UTC) at which the extract was assembled. Carried so the
/// citizen / printout has an unambiguous "as-of" anchor.
/// </param>
public sealed record PersonalAccountExtractDto(
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string AccountCodeSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SolicitantSqid,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<PersonalAccountYearDto> Years,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal GrandTotalContributions,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int GrandTotalMonths,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime GeneratedAtUtc);

/// <summary>
/// R0516 / TOR CF 02.04 — per-year roll-up of contribution entries surfaced by
/// the personal-account extract. The owning extract sorts the year buckets
/// DESC by <see cref="Year"/>; entries within each year are sorted ASC by
/// <see cref="PersonalAccountEntryDto.Month"/>.
/// </summary>
/// <param name="Year">Calendar year of the aggregation.</param>
/// <param name="Months">
/// Count of distinct months (1..12) inside <see cref="Year"/> that hold at
/// least one entry. Different from <see cref="Entries"/>.Count when multiple
/// sources (e.g. employer + DEC 100) report the same month.
/// </param>
/// <param name="TotalContributionBase">
/// Sum of <c>PersonalAccountEntry.ContributionBaseAmount</c> across every
/// entry inside <see cref="Year"/> (MDL). Drives the "average monthly base"
/// projection on the citizen-portal dashboard.
/// </param>
/// <param name="TotalContributionPaid">
/// Sum of <c>PersonalAccountEntry.ContributionPaidAmount</c> across every
/// entry inside <see cref="Year"/> (MDL).
/// </param>
/// <param name="Entries">
/// Underlying entry rows in ASC month order. Carried so the UI can render
/// the per-source detail rows; the citizen-portal mockups collapse multi-row
/// months into a single line by default and let the user expand on demand.
/// </param>
public sealed record PersonalAccountYearDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Year,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Months,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalContributionBase,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalContributionPaid,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<PersonalAccountEntryDto> Entries);

/// <summary>
/// R0516 / TOR CF 02.04 — one contribution row surfaced by the personal-account
/// extract. The entry's surrogate id is never exposed (the entity is internal —
/// the extract is the only public surface) so the DTO carries only the natural
/// (Year+Month+Source) coordinates plus the two monetary columns.
/// </summary>
/// <param name="Month">Calendar month, 1..12.</param>
/// <param name="ContributionBaseAmount">
/// Gross income subject to contribution for the month (MDL).
/// </param>
/// <param name="ContributionPaidAmount">
/// Actual contribution paid for the month (MDL).
/// </param>
/// <param name="SourceCode">
/// Stable source code identifying the system / process that produced the row
/// (e.g. <c>"EMPLOYER_REPORT"</c>, <c>"DEC100"</c>, <c>"INDIVIDUAL_PAYMENT"</c>).
/// </param>
public sealed record PersonalAccountEntryDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int Month,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal ContributionBaseAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal ContributionPaidAmount,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string SourceCode);
