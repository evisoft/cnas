using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts.Interop;

// ────────────────────────────────────────────────────────────────────────────
// R1702-R1708 / TOR CF 14.12 / Annex 4 — DTOs for the second batch of Annex-4
// B2B interop operations. The eight new operations follow the same no-PII /
// hash-prefix discipline as the R0634 batch: every response carries a
// forensic IDNP/IDNO hash prefix in place of the raw national identifier so
// audit forensics can correlate across rows without ever echoing the
// identifier back to the caller. Decision identifiers are Sqid-encoded; the
// raw <c>BenefitDecision.Id</c> never leaves the system boundary.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R1702 / TOR CF 14.12 / Annex 4 — request envelope for
/// <c>GetActiveDecisions</c>. Carries the lookup IDNP in the POST body so the
/// identifier never appears in URL paths, query strings, reverse-proxy
/// access logs, or the CDN edge. Same shape and discipline as
/// <see cref="InteropIdnpRequestDto"/> — kept as a distinct type so the
/// FluentValidation pipeline can wire a per-op validator without sharing
/// rules across unrelated request envelopes.
/// </summary>
/// <param name="Idnp">
/// Moldovan IDNP (13 digits, mod-10 checksum). Validated at the boundary;
/// malformed values surface as <c>INVALID_IDNP</c>. Never echoed back in any
/// response shape.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record ActiveDecisionsRequestDto(string Idnp);

/// <summary>
/// R1703 / TOR CF 14.12 / Annex 4 — request envelope for
/// <c>GetPaymentStatus</c>. Carries the Sqid-encoded decision handle plus
/// the reporting-month bucket the caller is probing.
/// </summary>
/// <param name="DecisionSqid">
/// Opaque Sqid-encoded <c>BenefitDecision</c> identifier. The decoder
/// rejects invalid Sqids with <c>INVALID_SQID</c>; rebinding the alphabet
/// is a breaking external-contract change. Sqids do not leak business
/// volume (sequential decision id counts), so they are safe to expose.
/// </param>
/// <param name="Period">
/// Reporting month (<c>day = 1</c>). The day component is ignored — only
/// the <c>Year</c> + <c>Month</c> tuple are consulted. Out-of-range months
/// surface as <c>INVALID_DATE_RANGE</c>.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record PaymentStatusRequestDto(
    string DecisionSqid,
    DateOnly Period);

/// <summary>
/// R1704 / TOR CF 14.12 / Annex 4 — request envelope for
/// <c>GetPayerData</c>. The <see cref="TaxpayerCode"/> may be an IDNP (a
/// natural-person payer — self-employed contributor) or an IDNO (a legal
/// entity). Both shapes are 13 digits; the service inspects the code
/// shape at the boundary and dispatches accordingly. Mixed-format payloads
/// (letters, dashes, ...) surface as <c>INVALID_IDNP</c> or
/// <c>INVALID_IDNO</c> depending on which shape was attempted.
/// </summary>
/// <param name="TaxpayerCode">
/// 13-digit IDNP or IDNO. Never echoed back in any response shape.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record PayerDataRequestDto(string TaxpayerCode);

/// <summary>
/// R1705 / TOR CF 14.12 / Annex 4 — request envelope for
/// <c>IsBenefitBeneficiary</c>. Carries the citizen IDNP plus the stable
/// <c>BenefitType</c> enum-name string the caller is probing
/// (<c>OldAgePension</c>, <c>UnemploymentAllowance</c>, ...). Unknown
/// benefit-type names surface as <c>VALIDATION_FAILED</c>.
/// </summary>
/// <param name="Idnp">Moldovan IDNP (13 digits, mod-10 checksum).</param>
/// <param name="BenefitType">
/// Stable enum-name string of the benefit type to probe. The wire form is
/// the <c>BenefitType.ToString()</c> output so the caller does not own the
/// numeric mapping.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record IsBenefitBeneficiaryRequestDto(
    string Idnp,
    string BenefitType);

/// <summary>
/// R1706 / TOR CF 14.12 / Annex 4 — request envelope for
/// <c>GetContributionPaymentInfo</c>. Probes the legal-entity payer
/// declaration / payment ledger for a single reporting month. The IDNO
/// must point at an active <c>Payer</c> row; closed / cancelled payers
/// surface as <c>NOT_FOUND</c>.
/// </summary>
/// <param name="Idno">Moldovan IDNO (13 digits) of the legal-entity payer.</param>
/// <param name="Period">
/// Reporting month (<c>day = 1</c>). Day component ignored.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record ContributionPaymentInfoRequestDto(
    string Idno,
    DateOnly Period);

/// <summary>
/// R1707 / TOR CF 14.12 / Annex 4 — request envelope for
/// <c>GetLegalApplicableForm</c>. Used by the bilateral social-security
/// agreement workflow to resolve which form (A1, E101, ...) applies to a
/// citizen working under one of the agreement-partner countries. Agreement
/// codes are stable strings of the shape <c>{ISO}_{MD}_{YEAR}</c>
/// (e.g. <c>RO_MD_2006</c>, <c>DE_MD_2018</c>).
/// </summary>
/// <param name="Idnp">Moldovan IDNP (13 digits).</param>
/// <param name="AgreementCode">
/// Stable bilateral-agreement code. Unknown codes surface as
/// <c>NOT_FOUND</c>; malformed codes surface as <c>VALIDATION_FAILED</c>.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record LegalApplicableFormRequestDto(
    string Idnp,
    string AgreementCode);

/// <summary>
/// R1702 / Annex 4 / <c>GetActiveDecisions</c> — response shape: one row
/// per currently-active benefit decision (across all benefit kinds) for the
/// resolved citizen. Each row is a denormalised projection of the
/// <c>BenefitDecision</c> aggregate suitable for an RSP / IPS portal
/// dashboard. Inactive (cancelled, expired, superseded) decisions are
/// excluded — the caller can request a historical view via a separate op
/// (deferred).
/// </summary>
/// <param name="IdnpHashPrefix">
/// First 8 hex characters of the deterministic IDNP hash. Same discipline
/// as <see cref="InsuredPersonStatusDto.IdnpHashPrefix"/>.
/// </param>
/// <param name="Decisions">
/// Decision rows sorted by <see cref="ActiveDecisionEntryDto.EffectiveFrom"/>
/// descending (newest first). Empty when the citizen has no currently-active
/// decisions on file.
/// </param>
/// <param name="AsOfUtc">
/// Server timestamp (UTC) at which the snapshot was assembled. Carried so
/// the caller can record an unambiguous as-of anchor.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record ActiveDecisionsDto(
    string IdnpHashPrefix,
    IReadOnlyList<ActiveDecisionEntryDto> Decisions,
    DateTime AsOfUtc);

/// <summary>
/// R1702 / Annex 4 — one row inside <see cref="ActiveDecisionsDto.Decisions"/>.
/// Carries the per-decision projection: opaque handle, benefit kind, decision
/// number, effective-window and the operational monthly amount.
/// </summary>
/// <param name="DecisionSqid">
/// Opaque Sqid handle for the <c>BenefitDecision</c> row — safe to expose
/// (does not leak sequential decision counts). Callers feed this back into
/// <c>GetPaymentStatus</c> to drill into a specific decision.
/// </param>
/// <param name="BenefitType">
/// Stable enum-name string of the benefit kind
/// (<c>OldAgePension</c>, <c>UnemploymentAllowance</c>, ...). Mirrors the
/// <c>BenefitType</c> wire vocabulary used elsewhere in this DTO family.
/// </param>
/// <param name="DecisionNumber">
/// Human-readable decision number printed on the citizen-facing notice
/// (e.g. <c>"D-2024-0123"</c>). Stable; never re-used across decisions.
/// </param>
/// <param name="EffectiveFrom">First day on which the decision pays out.</param>
/// <param name="EffectiveUntil">
/// Last day on which the decision pays out, or <c>null</c> for open-ended
/// decisions (old-age pension, disability with permanent classification).
/// </param>
/// <param name="MonthlyAmountMdl">
/// Operational monthly amount paid by the decision (MDL). The actual
/// per-month payment may differ on the upper / lower tail of the effective
/// window (pro-rata) but this is the steady-state figure printed on the
/// citizen notice.
/// </param>
/// <param name="Status">
/// Stable enum-name string of the decision lifecycle state. The DTO surface
/// only ever returns active states (<c>Active</c>, <c>Suspended</c>) — the
/// op filters out terminal states.
/// </param>
/// <param name="IssuingOffice">
/// Display name of the CNAS branch / office that issued the decision (e.g.
/// <c>"CTAS Chișinău"</c>). Free-form text drawn from the
/// <c>CnasBranch.DisplayName</c> column.
/// </param>
public sealed record ActiveDecisionEntryDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DecisionSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string BenefitType,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DecisionNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly EffectiveFrom,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? EffectiveUntil,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal MonthlyAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string IssuingOffice);

/// <summary>
/// R1703 / Annex 4 / <c>GetPaymentStatus</c> — response shape: the per-month
/// disbursement status for a single benefit decision. The caller drills into
/// a specific decision (from <c>GetActiveDecisions</c>) for a single
/// reporting month; bulk windows are intentionally NOT supported on this op
/// — that surface is covered by the citizen-portal payment-history endpoint.
/// </summary>
/// <param name="DecisionSqid">
/// Echo of the requested decision handle. Sqid form so the caller can
/// correlate the response with the request without parsing the URL.
/// </param>
/// <param name="Period">Reporting-month tuple (<c>day = 1</c>).</param>
/// <param name="PaymentStatus">
/// Stable enum-name string of the payment lifecycle state
/// (<c>Pending</c>, <c>Paid</c>, <c>Returned</c>, <c>Suspended</c>).
/// </param>
/// <param name="AmountMdl">
/// Per-month disbursement amount (MDL). May be zero when the decision is
/// suspended for this period.
/// </param>
/// <param name="PaidDate">
/// Date on which the bank / postal channel confirmed delivery, or
/// <c>null</c> when the payment is still <c>Pending</c> / <c>Returned</c>.
/// </param>
/// <param name="ChannelKind">
/// Stable enum-name string of the disbursement channel
/// (<c>BankTransfer</c>, <c>PostalOrder</c>, <c>CashPayout</c>).
/// </param>
/// <param name="ReceiptReference">
/// Channel-specific receipt reference (bank transaction id, postal-order
/// receipt) up to 64 characters. <c>null</c> when no receipt was issued yet.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record PaymentStatusDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string DecisionSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly Period,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PaymentStatus,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal AmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? PaidDate,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ChannelKind,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? ReceiptReference);

/// <summary>
/// R1704 / Annex 4 / <c>GetPayerData</c> — response shape: the per-payer
/// registry snapshot. <see cref="PayerKind"/> distinguishes natural-person
/// (self-employed contributor) from legal-entity (company / institution)
/// branches; the <see cref="CountOfInsuredEmployees"/> field is only
/// meaningful on the legal-entity branch and is zero on the natural-person
/// branch.
/// </summary>
/// <param name="TaxpayerHashPrefix">
/// First 8 hex characters of the deterministic IDNP / IDNO hash. The caller
/// cannot tell from the prefix whether the payload originated from an IDNP
/// or IDNO probe — that distinction surfaces only on
/// <see cref="PayerKind"/>.
/// </param>
/// <param name="PayerKind">
/// Stable enum-name string of the payer category
/// (<c>NaturalPerson</c>, <c>LegalEntity</c>).
/// </param>
/// <param name="DisplayName">
/// Public-facing payer name (<c>Solicitant.DisplayName</c> for naturals,
/// <c>Payer.DisplayName</c> for legal entities). Free-form text — no PII
/// guarantees beyond what the source row carries.
/// </param>
/// <param name="RegistrationDate">
/// Date the payer record was created in CNAS. Mirrors the source row's
/// <c>CreatedAtUtc.Date</c>.
/// </param>
/// <param name="Status">
/// Stable enum-name string of the payer lifecycle state
/// (<c>Active</c>, <c>Suspended</c>, <c>Closed</c>).
/// </param>
/// <param name="CountOfInsuredEmployees">
/// Number of active insured persons currently bound to the legal-entity
/// payer. Zero for natural-person payers. Zero for legal entities with no
/// active employment relationships.
/// </param>
/// <param name="LastDeclarationMonth">
/// Reporting-month tuple of the most recent declaration filed by the payer,
/// or <c>null</c> when no declarations are on file.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record PayerDataDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string TaxpayerHashPrefix,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string PayerKind,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string DisplayName,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly RegistrationDate,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int CountOfInsuredEmployees,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? LastDeclarationMonth);

/// <summary>
/// R1705 / Annex 4 / <c>IsBenefitBeneficiary</c> — response shape: a
/// boolean probe + decoded explanation. The caller (SIVE / SIAAS) uses this
/// to gate eligibility for downstream benefits ("am I currently eligible
/// for child-allowance top-ups?"). The <see cref="DecisionSqid"/> is
/// populated only on the affirmative branch so the caller can drill into
/// the underlying decision via <c>GetPaymentStatus</c>.
/// </summary>
/// <param name="IdnpHashPrefix">
/// First 8 hex characters of the deterministic IDNP hash. Same discipline
/// as <see cref="InsuredPersonStatusDto.IdnpHashPrefix"/>.
/// </param>
/// <param name="BenefitType">
/// Echo of the probed benefit-type enum-name string. Carried so the caller
/// can correlate the response with the request without parsing the URL.
/// </param>
/// <param name="IsBeneficiary">
/// <c>true</c> when an active decision pays out the probed
/// <c>BenefitType</c> to the citizen; <c>false</c> otherwise.
/// </param>
/// <param name="Reason">
/// Short human-readable explanation when <see cref="IsBeneficiary"/> is
/// <c>false</c> (<c>"NO_ACTIVE_DECISION"</c>,
/// <c>"DECISION_SUSPENDED"</c>, <c>"UNKNOWN_IDNP"</c>). Empty string when
/// <see cref="IsBeneficiary"/> is <c>true</c>.
/// </param>
/// <param name="EvaluationDate">
/// Date on which the evaluation was performed (server's UTC clock,
/// truncated to the day component). Carried so the caller can record an
/// unambiguous as-of anchor.
/// </param>
/// <param name="DecisionSqid">
/// Opaque Sqid handle of the active decision driving the affirmative
/// answer, or <c>null</c> when <see cref="IsBeneficiary"/> is <c>false</c>.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record IsBenefitBeneficiaryDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string IdnpHashPrefix,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string BenefitType,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool IsBeneficiary,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Reason,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly EvaluationDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? DecisionSqid);

/// <summary>
/// R1706 / Annex 4 / <c>GetContributionPaymentInfo</c> — response shape:
/// the per-month declaration + payment roll-up for a legal-entity payer.
/// <see cref="Outstanding"/> is the arithmetic difference
/// <c>TotalDueMdl - TotalPaidMdl</c>, clamped at zero — a negative would
/// indicate an overpayment which surfaces as zero outstanding plus a
/// <see cref="LatePenaltyMdl"/> of zero. Refund handling is a separate op
/// (deferred).
/// </summary>
/// <param name="IdnoHashPrefix">
/// First 8 hex characters of the deterministic IDNO hash. Same discipline
/// as <see cref="InsuredPersonStatusDto.IdnpHashPrefix"/>.
/// </param>
/// <param name="Period">Reporting-month tuple (<c>day = 1</c>).</param>
/// <param name="DeclarationStatus">
/// Stable enum-name string of the declaration lifecycle state
/// (<c>Filed</c>, <c>NotFiled</c>, <c>Late</c>).
/// </param>
/// <param name="TotalDueMdl">
/// Total contribution amount due for the reporting month (MDL). Mirrors
/// <c>MonthlyContributionCalculation.TotalDue</c>.
/// </param>
/// <param name="TotalPaidMdl">
/// Total contribution amount paid against the reporting month (MDL).
/// </param>
/// <param name="Outstanding">
/// <c>max(TotalDueMdl - TotalPaidMdl, 0)</c> (MDL). Non-negative by
/// construction; overpayments surface as zero.
/// </param>
/// <param name="LatePenaltyMdl">
/// Sum of late-payment penalties accrued against the reporting month
/// (MDL). Mirrors the rolled-up <c>LatePaymentPenalty</c> rows for the
/// (payer, period) tuple.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record ContributionPaymentInfoDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string IdnoHashPrefix,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly Period,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DeclarationStatus,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalDueMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalPaidMdl,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal Outstanding,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal LatePenaltyMdl);

/// <summary>
/// R1707 / Annex 4 / <c>GetLegalApplicableForm</c> — response shape: the
/// applicable EU-equivalent posting form (A1, E101, ...) for a citizen
/// working under a bilateral social-security agreement. Returns
/// <c>NotApplicable</c> when no agreement is in force for the requested
/// pair (citizen, agreement-code).
/// </summary>
/// <param name="IdnpHashPrefix">
/// First 8 hex characters of the deterministic IDNP hash. Same discipline
/// as <see cref="InsuredPersonStatusDto.IdnpHashPrefix"/>.
/// </param>
/// <param name="AgreementCode">
/// Echo of the bilateral-agreement code probed (<c>RO_MD_2006</c>, ...).
/// </param>
/// <param name="ApplicableForm">
/// Stable enum-name string of the applicable form
/// (<c>A1Equivalent</c>, <c>E101Equivalent</c>, <c>NotApplicable</c>).
/// </param>
/// <param name="FormSerialNumber">
/// Serial number of the issued form (up to 32 characters), or <c>null</c>
/// when the form has not yet been issued / is not applicable.
/// </param>
/// <param name="IssueDate">
/// Date the form was issued, or <c>null</c> when not applicable.
/// </param>
/// <param name="ValidUntil">
/// Date the form expires (inclusive), or <c>null</c> when the form is
/// open-ended or not applicable.
/// </param>
/// <param name="HostCountryCode">
/// ISO-3166-1 alpha-2 country code of the agreement partner
/// (e.g. <c>RO</c>, <c>DE</c>, <c>TR</c>).
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record LegalApplicableFormDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string IdnpHashPrefix,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string AgreementCode,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string ApplicableForm,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string? FormSerialNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? IssueDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ValidUntil,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string HostCountryCode);

/// <summary>
/// R1708 / Annex 4 / <c>GetWorkInsurancePeriod</c> — response shape: the
/// aggregate insured-employment-period roll-up for a citizen on RM
/// territory. The <see cref="TotalMonths"/> figure is the de-duplicated
/// count of months in which at least one insured-employment row was active;
/// the <see cref="PeriodCount"/> figure is the count of distinct
/// continuous spells.
/// </summary>
/// <param name="IdnpHashPrefix">
/// First 8 hex characters of the deterministic IDNP hash. Same discipline
/// as <see cref="InsuredPersonStatusDto.IdnpHashPrefix"/>.
/// </param>
/// <param name="TotalMonths">
/// De-duplicated count of insured-employment months on RM territory. Never
/// negative; zero when no employment is on file.
/// </param>
/// <param name="FirstInsuredMonth">
/// Earliest insured-employment month, or <c>null</c> when no employment is
/// on file.
/// </param>
/// <param name="LastInsuredMonth">
/// Latest insured-employment month, or <c>null</c> when no employment is on
/// file. Pair with <see cref="FirstInsuredMonth"/> to derive the total span
/// covered by the roll-up.
/// </param>
/// <param name="CurrentlyInsured">
/// <c>true</c> when at least one insured-employment row is still active as
/// of the evaluation date.
/// </param>
/// <param name="PeriodCount">
/// Number of distinct continuous insured-employment spells. May be greater
/// than 1 even when <see cref="CurrentlyInsured"/> is <c>true</c> — gaps
/// between spells are common.
/// </param>
[SensitivityClassification(SensitivityLabel.Confidential)]
public sealed record WorkInsurancePeriodDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string IdnpHashPrefix,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    int TotalMonths,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? FirstInsuredMonth,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? LastInsuredMonth,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    bool CurrentlyInsured,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    int PeriodCount);
