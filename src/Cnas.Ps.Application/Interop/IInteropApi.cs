using Cnas.Ps.Contracts.Interop;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Interop;

/// <summary>
/// R0634 / TOR CF 14.12 / Annex 4 — server-to-server interop façade that other
/// MGov systems (RSP, MoFin, IPS, SIVE, SIAAS, ...) call to query CNAS
/// state. The four canonical ops below cover the most-requested Annex-4
/// queries; the remaining Annex-4 ops (R1700-R1710) ride the same auth /
/// audit / hash-prefix discipline and will be added in a later batch.
/// </summary>
/// <remarks>
/// <para>
/// <b>No PII echo.</b> Every response surface uses the
/// <c>IdnpHashPrefix</c> field — the first 8 hex characters of the
/// deterministic IDNP HMAC — in place of the raw IDNP. Callers never see the
/// IDNP they asked about reflected back; this is deliberate so the response
/// log on the caller side does not become a secondary PII store.
/// </para>
/// <para>
/// <b>Audit per call.</b> Every successful (and many failure) calls write
/// one <c>INTEROP.{OPERATION}.QUERIED</c> audit row with
/// <see cref="Cnas.Ps.Core.Domain.AuditSeverity.Sensitive"/> severity. The
/// audit row carries only the hash prefix and the resolution outcome —
/// never the raw IDNP.
/// </para>
/// <para>
/// <b>Auth.</b> The HTTP controller is gated by an <c>InteropClient</c>
/// role placeholder. Real OAuth2 client-credentials / mTLS binding lands in
/// a follow-up batch — see TODO §11 R1709.
/// </para>
/// </remarks>
public interface IInteropApi
{
    /// <summary>
    /// R0634 / Annex 4 — implements <c>GetInsuredPersonStatus</c>. Given an
    /// IDNP, returns whether the citizen is registered with CNAS at all,
    /// the opaque account code of their personal-account aggregate when
    /// one exists, and a quick active-benefits count.
    /// </summary>
    /// <param name="idnp">Moldovan IDNP (13 digits + mod-10 checksum).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// A populated <see cref="InsuredPersonStatusDto"/> on success — the
    /// <c>IsRegistered=false</c> branch is also a success (the soft-404
    /// shape documented on the DTO). Failure with
    /// <see cref="ErrorCodes.InvalidIdnp"/> when the input fails IDNP
    /// validation.
    /// </returns>
    Task<Result<InsuredPersonStatusDto>> GetInsuredPersonStatusAsync(
        string idnp,
        CancellationToken ct = default);

    /// <summary>
    /// R0634 / Annex 4 — implements <c>GetContributionHistory</c>. Returns
    /// the contribution-month entries that fall inside the supplied
    /// inclusive window.
    /// </summary>
    /// <param name="idnp">Moldovan IDNP (13 digits + mod-10 checksum).</param>
    /// <param name="fromMonth">Inclusive lower bound of the window.</param>
    /// <param name="toMonth">
    /// Inclusive upper bound. Must be on or after <paramref name="fromMonth"/>;
    /// the spanned width must not exceed the validator window cap.
    /// </param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="ContributionHistoryDto"/> on success. Failure
    /// with <see cref="ErrorCodes.InvalidIdnp"/>,
    /// <see cref="ErrorCodes.InvalidDateRange"/>, or
    /// <see cref="ErrorCodes.NotFound"/> when the citizen is not registered.
    /// </returns>
    Task<Result<ContributionHistoryDto>> GetContributionHistoryAsync(
        string idnp,
        DateOnly fromMonth,
        DateOnly toMonth,
        CancellationToken ct = default);

    /// <summary>
    /// R0634 / Annex 4 — implements <c>GetBenefitsList</c>. Returns one row
    /// per distinct <c>BenefitType</c> the citizen has ever been paid (active
    /// or historical).
    /// </summary>
    /// <param name="idnp">Moldovan IDNP (13 digits + mod-10 checksum).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="BenefitsListDto"/> on success. Failure with
    /// <see cref="ErrorCodes.InvalidIdnp"/> or
    /// <see cref="ErrorCodes.NotFound"/> when the citizen is not registered.
    /// </returns>
    Task<Result<BenefitsListDto>> GetBenefitsListAsync(
        string idnp,
        CancellationToken ct = default);

    /// <summary>
    /// R0634 / Annex 4 — implements <c>GetPersonalAccountSnapshot</c>.
    /// Returns the cached lifetime totals from the citizen's
    /// <c>PersonalAccount</c> aggregate.
    /// </summary>
    /// <param name="idnp">Moldovan IDNP (13 digits + mod-10 checksum).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="PersonalAccountSnapshotDto"/> on success.
    /// Failure with <see cref="ErrorCodes.InvalidIdnp"/> or
    /// <see cref="ErrorCodes.NotFound"/> when the citizen is not registered
    /// or has no personal-account aggregate on file.
    /// </returns>
    Task<Result<PersonalAccountSnapshotDto>> GetPersonalAccountSnapshotAsync(
        string idnp,
        CancellationToken ct = default);

    /// <summary>
    /// R1702 / Annex 4 — implements <c>GetActiveDecisions</c>. Returns one
    /// row per currently-active benefit decision (across all benefit kinds)
    /// for the citizen identified by the supplied IDNP.
    /// </summary>
    /// <param name="idnp">Moldovan IDNP (13 digits + mod-10 checksum).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="ActiveDecisionsDto"/> on success — empty
    /// <c>Decisions</c> list when the citizen has no active decisions on
    /// file. Failure with <see cref="ErrorCodes.InvalidIdnp"/> on malformed
    /// input or <see cref="ErrorCodes.NotFound"/> when the citizen is not
    /// registered.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Deterministic stub.</b> The first-class <c>BenefitDecision</c>
    /// aggregate is partial (the decision registry lands in a follow-up
    /// batch). Until then this op deterministically returns an empty
    /// <c>Decisions</c> list for registered citizens — the API surface,
    /// validators, audit, and metrics are exercised end-to-end so the B2B
    /// consumer can wire integration tests against a stable shape.
    /// </para>
    /// </remarks>
    Task<Result<ActiveDecisionsDto>> GetActiveDecisionsAsync(
        string idnp,
        CancellationToken ct = default);

    /// <summary>
    /// R1703 / Annex 4 — implements <c>GetPaymentStatus</c>. Returns the
    /// per-month disbursement status for a single benefit decision
    /// identified by its Sqid handle.
    /// </summary>
    /// <param name="decisionSqid">Opaque Sqid handle of a <c>BenefitDecision</c>.</param>
    /// <param name="period">Reporting month (<c>day = 1</c>). Day component ignored.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="PaymentStatusDto"/> on success. Failure with
    /// <see cref="ErrorCodes.InvalidSqid"/> on a malformed handle,
    /// <see cref="ErrorCodes.InvalidDateRange"/> on a malformed period, or
    /// <see cref="ErrorCodes.NotFound"/> when the decision (or its
    /// payment for the period) is not on file.
    /// </returns>
    /// <remarks>
    /// <b>Deterministic stub.</b> Pending the <c>BenefitDecision</c>
    /// aggregate this op currently surfaces <c>NOT_FOUND</c> for every
    /// well-formed Sqid — see <see cref="GetActiveDecisionsAsync"/>.
    /// </remarks>
    Task<Result<PaymentStatusDto>> GetPaymentStatusAsync(
        string decisionSqid,
        DateOnly period,
        CancellationToken ct = default);

    /// <summary>
    /// R1704 / Annex 4 — implements <c>GetPayerData</c>. The
    /// <paramref name="taxpayerCode"/> may be an IDNP (natural-person
    /// self-employed payer) or an IDNO (legal entity). The implementation
    /// inspects the code shape and dispatches accordingly.
    /// </summary>
    /// <param name="taxpayerCode">13-digit IDNP or IDNO.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="PayerDataDto"/> on success. Failure with
    /// <see cref="ErrorCodes.ValidationFailed"/> on malformed input or
    /// <see cref="ErrorCodes.NotFound"/> when the payer is not on file.
    /// </returns>
    Task<Result<PayerDataDto>> GetPayerDataAsync(
        string taxpayerCode,
        CancellationToken ct = default);

    /// <summary>
    /// R1705 / Annex 4 — implements <c>IsBenefitBeneficiary</c>. Returns a
    /// boolean probe + decoded explanation.
    /// </summary>
    /// <param name="idnp">Moldovan IDNP (13 digits + mod-10 checksum).</param>
    /// <param name="benefitType">Stable enum-name string of the benefit type to probe.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="IsBenefitBeneficiaryDto"/> on success.
    /// Failure with <see cref="ErrorCodes.InvalidIdnp"/> on a malformed
    /// IDNP or <see cref="ErrorCodes.ValidationFailed"/> on an unknown
    /// benefit-type string.
    /// </returns>
    Task<Result<IsBenefitBeneficiaryDto>> IsBenefitBeneficiaryAsync(
        string idnp,
        string benefitType,
        CancellationToken ct = default);

    /// <summary>
    /// R1706 / Annex 4 — implements <c>GetContributionPaymentInfo</c>.
    /// Returns the per-month declaration + payment roll-up for a legal
    /// entity identified by IDNO.
    /// </summary>
    /// <param name="idno">Moldovan IDNO (13 digits).</param>
    /// <param name="period">Reporting month (<c>day = 1</c>).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="ContributionPaymentInfoDto"/> on success.
    /// Failure with <see cref="ErrorCodes.InvalidIdno"/> on a malformed
    /// IDNO, <see cref="ErrorCodes.InvalidDateRange"/> on a malformed
    /// period, or <see cref="ErrorCodes.NotFound"/> when the payer is not
    /// on file.
    /// </returns>
    Task<Result<ContributionPaymentInfoDto>> GetContributionPaymentInfoAsync(
        string idno,
        DateOnly period,
        CancellationToken ct = default);

    /// <summary>
    /// R1707 / Annex 4 — implements <c>GetLegalApplicableForm</c>. Returns
    /// the applicable EU-equivalent posting form (A1, E101, ...) for a
    /// citizen working under one of the bilateral social-security
    /// agreement-partner countries.
    /// </summary>
    /// <param name="idnp">Moldovan IDNP (13 digits + mod-10 checksum).</param>
    /// <param name="agreementCode">Stable bilateral-agreement code (e.g. <c>RO_MD_2006</c>).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="LegalApplicableFormDto"/> on success — the
    /// <c>NotApplicable</c> branch is also a success (the consumer expects
    /// the boolean shape). Failure with <see cref="ErrorCodes.InvalidIdnp"/>
    /// on a malformed IDNP or <see cref="ErrorCodes.ValidationFailed"/> on
    /// a malformed agreement code.
    /// </returns>
    Task<Result<LegalApplicableFormDto>> GetLegalApplicableFormAsync(
        string idnp,
        string agreementCode,
        CancellationToken ct = default);

    /// <summary>
    /// R1708 / Annex 4 — implements <c>GetWorkInsurancePeriod</c>. Returns
    /// the aggregate insured-employment-period roll-up for a citizen on RM
    /// territory.
    /// </summary>
    /// <param name="idnp">Moldovan IDNP (13 digits + mod-10 checksum).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// Populated <see cref="WorkInsurancePeriodDto"/> on success. Failure
    /// with <see cref="ErrorCodes.InvalidIdnp"/> on a malformed IDNP or
    /// <see cref="ErrorCodes.NotFound"/> when the citizen is not
    /// registered.
    /// </returns>
    Task<Result<WorkInsurancePeriodDto>> GetWorkInsurancePeriodAsync(
        string idnp,
        CancellationToken ct = default);
}
