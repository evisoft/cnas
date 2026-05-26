using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0517 — Benefit-payment status (authenticated self-service)
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0517 / TOR CF 02.05 — query envelope for the citizen "status of benefit
/// payments" endpoint. All three filter slots are optional; the service
/// substitutes sensible defaults (last 12 months + next 3 months window, no
/// benefit-type filter) when a slot is omitted. Bounded at 36 months total
/// via the matching validator (<c>BenefitPaymentStatusQueryDtoValidator</c>)
/// so a single call cannot scan the entire history of a long-tenured
/// beneficiary.
/// </summary>
/// <param name="FromMonth">
/// Inclusive lower bound of the window. When omitted the service defaults to
/// 12 months before <c>UtcNow</c> rounded to the first of the month.
/// </param>
/// <param name="ToMonth">
/// Inclusive upper bound of the window. When omitted the service defaults to
/// 3 months after <c>UtcNow</c> rounded to the first of the month.
/// </param>
/// <param name="Type">
/// Optional benefit-type filter — when supplied the service returns only
/// payments of the matching <c>BenefitType</c> (stable enum name, e.g.
/// <c>"OldAgePension"</c>). When omitted or set to <c>null</c> every type is
/// included. Unrecognised values surface as a validation failure at the
/// service boundary.
/// </param>
public sealed record BenefitPaymentStatusQueryDto(
    DateOnly? FromMonth = null,
    DateOnly? ToMonth = null,
    string? Type = null);

/// <summary>
/// R0517 / TOR CF 02.05 — citizen-facing aggregated status of pension and
/// allowance payments. Each call returns the per-row list of payments inside
/// the requested window plus two roll-up totals (last 12 paid, next 3
/// scheduled) and the as-of timestamp. The endpoint is authenticated; the
/// caller's Solicitant is resolved server-side from <c>ICallerContext</c>
/// (citizen surface) or supplied explicitly for the admin /
/// utilizator-autorizat path gated by <c>BenefitPayment.ReadAny</c>.
/// </summary>
/// <param name="SolicitantSqid">
/// Sqid-encoded surrogate id of the beneficiary <c>Solicitant</c>. Included
/// so the admin surface can cross-link to the Solicitant detail page; the
/// citizen self-service surface treats it as opaque.
/// </param>
/// <param name="Payments">
/// All payment rows inside the requested window, sorted by
/// <see cref="BenefitPaymentDto.PaymentMonth"/> DESC. May be empty when the
/// beneficiary has no payments on file inside the window.
/// </param>
/// <param name="TotalPaidLast12Months">
/// Sum of <c>NetAmount</c> across every row with <c>Status = Paid</c> whose
/// <c>PaymentMonth</c> falls inside the last 12 months relative to
/// <see cref="GeneratedAtUtc"/>. Independent of the requested
/// <c>FromMonth</c> / <c>ToMonth</c> window so the rolling total stays
/// comparable across queries.
/// </param>
/// <param name="TotalScheduledNext3Months">
/// Sum of <c>NetAmount</c> across every row with <c>Status = Scheduled</c>
/// whose <c>PaymentMonth</c> falls inside the next 3 months relative to
/// <see cref="GeneratedAtUtc"/>. Independent of the requested window for the
/// same reason as <see cref="TotalPaidLast12Months"/>.
/// </param>
/// <param name="GeneratedAtUtc">
/// Server timestamp (UTC) at which the status payload was assembled. Carried
/// so the printout has an unambiguous "as-of" anchor.
/// </param>
public sealed record BenefitPaymentStatusDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string SolicitantSqid,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    IReadOnlyList<BenefitPaymentDto> Payments,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalPaidLast12Months,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TotalScheduledNext3Months,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateTime GeneratedAtUtc);

/// <summary>
/// R0517 / TOR CF 02.05 — one benefit-payment ledger row surfaced by the
/// citizen / admin status endpoints. The Sqid-encoded
/// <see cref="Id"/> identifies the underlying <c>BenefitPayment</c> aggregate
/// per CLAUDE.md RULE 3 — citizens can reference an individual row when
/// challenging a returned or missing disbursement.
/// </summary>
/// <param name="Id">
/// Sqid-encoded surrogate id of the underlying <c>BenefitPayment</c>. Opaque
/// to clients — used purely as a stable handle for follow-up support tickets.
/// </param>
/// <param name="BenefitType">
/// Stable enum-name representation of the <c>BenefitType</c>
/// (<c>OldAgePension</c>, <c>ChildAllowance</c>, ...). The wire shape is the
/// enum name (not the numeric value) so the client can switch on a
/// self-describing label.
/// </param>
/// <param name="PaymentMonth">
/// Calendar month the payment covers (day component always 1).
/// </param>
/// <param name="GrossAmount">Gross amount before tax (MDL).</param>
/// <param name="NetAmount">Net amount actually paid (MDL).</param>
/// <param name="TaxWithheld">Tax withheld at source (MDL).</param>
/// <param name="Status">
/// Stable enum-name representation of the <c>BenefitPaymentStatus</c>
/// (<c>Scheduled</c>, <c>Issued</c>, <c>Paid</c>, <c>Returned</c>,
/// <c>Cancelled</c>).
/// </param>
/// <param name="Method">
/// Stable enum-name representation of the <c>BenefitPaymentMethod</c>
/// (<c>BankTransfer</c>, <c>PostalOrder</c>, <c>Cash</c>).
/// </param>
/// <param name="BankAccountIban">
/// Beneficiary IBAN — present only when <see cref="Method"/> is
/// <c>BankTransfer</c>. Carries Restricted sensitivity; clients must mask
/// all but the last four characters when rendering. The endpoint is
/// authenticated, so the raw value is supplied; the
/// <c>SensitivityHeaderMiddleware</c> writes an
/// <c>SENSITIVITY.RESTRICTED_ACCESS</c> audit row for every response that
/// carries a Restricted field.
/// </param>
/// <param name="PostalOrderNumber">
/// Postal-order serial — present only when <see cref="Method"/> is
/// <c>PostalOrder</c>. Confidential sensitivity at the DTO boundary.
/// </param>
/// <param name="IssuedDate">
/// Date the channel handed the payment to the bank or postal service.
/// <c>null</c> on Scheduled rows.
/// </param>
/// <param name="PaidDate">
/// Date the channel confirmed receipt by the beneficiary. <c>null</c> until
/// <see cref="Status"/> reaches <c>Paid</c>.
/// </param>
/// <param name="ReturnedDate">
/// Date the channel reported the payment as returned. <c>null</c> on healthy
/// rows.
/// </param>
/// <param name="ReturnReason">
/// Human-readable rationale captured alongside a Returned / Cancelled status.
/// <c>null</c> on healthy rows.
/// </param>
public sealed record BenefitPaymentDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Id,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string BenefitType,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly PaymentMonth,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal GrossAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal NetAmount,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    decimal TaxWithheld,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Method,
    [property: SensitivityClassification(SensitivityLabel.Restricted)]
    string? BankAccountIban,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? PostalOrderNumber,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? IssuedDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? PaidDate,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    DateOnly? ReturnedDate,
    [property: SensitivityClassification(SensitivityLabel.Confidential)]
    string? ReturnReason);
