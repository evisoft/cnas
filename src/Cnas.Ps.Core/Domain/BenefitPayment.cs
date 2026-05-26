namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0517 / TOR CF 02.05 — one citizen benefit-payment ledger row. Each row
/// represents the disbursement (paid, scheduled, returned, cancelled) of a
/// specific <see cref="BenefitType"/> for a specific calendar month attributed
/// to a beneficiary <see cref="Solicitant"/>. The authenticated
/// <c>GET /api/self-service/benefit-payments</c> surface (R0517) projects these
/// rows into the citizen-portal "status of payments" page and the admin
/// counterpart at <c>GET /api/admin/benefit-payments/{solicitantSqid}</c>
/// gated by the <c>BenefitPayment.ReadAny</c> permission.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate entity.</b> Pension and allowance computation (R0514) and
/// contribution history (R0516) live elsewhere — those rows describe the
/// citizen's eligibility envelope and contribution ledger. This entity is the
/// orthogonal disbursement ledger: who got paid what, when, through which
/// channel, with which downstream tracking number. It is intentionally
/// upstream-agnostic: the eventual MTreasury / IPS reconciliation adapter
/// (deferred) will write into and reconcile from these same rows.
/// </para>
/// <para>
/// <b>Natural key.</b> The triple <c>(BeneficiarySolicitantId, BenefitType,
/// PaymentMonth)</c> is unique — a Solicitant cannot have two payments for the
/// same benefit covering the same month. Multiple-channel resends after a
/// <see cref="BenefitPaymentStatus.Returned"/> event reuse the same row by
/// transitioning its <see cref="Status"/> rather than creating a duplicate.
/// Enforced via a composite unique index in
/// <c>Cnas.Ps.Infrastructure.Persistence.Configurations.BenefitPaymentConfiguration</c>.
/// </para>
/// <para>
/// <b>External id.</b> The entity implements <see cref="IExternalId"/> because
/// the output DTO (<c>Cnas.Ps.Contracts.BenefitPaymentDto.Id</c>) carries the
/// Sqid-encoded surrogate primary key per CLAUDE.md RULE 3 — citizens may
/// reference an individual payment row when challenging a returned or missing
/// disbursement.
/// </para>
/// <para>
/// <b>Sensitive columns.</b> <see cref="BankAccountIban"/> and
/// <see cref="PostalOrderNumber"/> are channel artefacts treated as Restricted
/// at the DTO boundary (annotated on
/// <c>Cnas.Ps.Contracts.BenefitPaymentDto</c>). Application-level encryption
/// for the IBAN column is deferred to the same wave that retroactively
/// encrypts other IBANs (R0184 follow-up); the column is plain decimal at
/// rest in this iteration.
/// </para>
/// </remarks>
public sealed class BenefitPayment : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the beneficiary <see cref="Solicitant"/> — the
    /// citizen who receives (or was scheduled to receive) the disbursement.
    /// </summary>
    public long BeneficiarySolicitantId { get; set; }

    /// <summary>
    /// Classification of the payment (old-age pension, child allowance, ...).
    /// Persisted as <c>int</c> via the EF configuration.
    /// </summary>
    public BenefitType BenefitType { get; set; }

    /// <summary>
    /// Calendar month that the payment covers. By convention the day component
    /// is always 1 — the canonical "first of the month" anchor that the totals
    /// projection (last 12 / next 3) windows against. Application-layer code
    /// is responsible for normalising the day to 1 before persistence; the
    /// schema does not enforce it because PostgreSQL has no native
    /// "month-only" type.
    /// </summary>
    public DateOnly PaymentMonth { get; set; }

    /// <summary>
    /// Gross amount of the disbursement before tax (MDL).
    /// </summary>
    public decimal GrossAmount { get; set; }

    /// <summary>
    /// Net amount actually paid (MDL) — equal to
    /// <see cref="GrossAmount"/> minus <see cref="TaxWithheld"/> in normal
    /// rounding. Stored explicitly so the channel-reconciliation step can
    /// re-validate the math without re-computing tax rates.
    /// </summary>
    public decimal NetAmount { get; set; }

    /// <summary>
    /// Tax withheld at source (MDL). Zero for non-taxable benefits (e.g. some
    /// allowances per Annex 2.5).
    /// </summary>
    public decimal TaxWithheld { get; set; }

    /// <summary>
    /// Lifecycle status — <see cref="BenefitPaymentStatus.Scheduled"/>,
    /// <see cref="BenefitPaymentStatus.Issued"/>,
    /// <see cref="BenefitPaymentStatus.Paid"/>,
    /// <see cref="BenefitPaymentStatus.Returned"/>, or
    /// <see cref="BenefitPaymentStatus.Cancelled"/>. Drives the totals
    /// projection on the status DTO and the inevitable upstream-ledger
    /// reconciliation.
    /// </summary>
    public BenefitPaymentStatus Status { get; set; }

    /// <summary>
    /// Disbursement channel — bank transfer, postal order, or cash. Decides
    /// which of the two channel-specific fields below is populated.
    /// </summary>
    public BenefitPaymentMethod Method { get; set; }

    /// <summary>
    /// Beneficiary IBAN when <see cref="Method"/> is
    /// <see cref="BenefitPaymentMethod.BankTransfer"/>; <c>null</c> otherwise.
    /// Stored as the canonical UPPERCASE form per ISO-13616. Surfaced on the
    /// authenticated DTO with a Restricted sensitivity label — clients must
    /// mask all but the last four characters when rendering.
    /// </summary>
    public string? BankAccountIban { get; set; }

    /// <summary>
    /// Postal-order serial number when <see cref="Method"/> is
    /// <see cref="BenefitPaymentMethod.PostalOrder"/>; <c>null</c> otherwise.
    /// Carries Confidential sensitivity at the DTO boundary.
    /// </summary>
    public string? PostalOrderNumber { get; set; }

    /// <summary>
    /// Date the channel handed the payment over to the beneficiary's bank or
    /// the postal service. Populated when <see cref="Status"/> reaches
    /// <see cref="BenefitPaymentStatus.Issued"/> and onward.
    /// </summary>
    public DateOnly? IssuedDate { get; set; }

    /// <summary>
    /// Date the channel confirmed receipt by the beneficiary. Populated when
    /// <see cref="Status"/> reaches <see cref="BenefitPaymentStatus.Paid"/>.
    /// </summary>
    public DateOnly? PaidDate { get; set; }

    /// <summary>
    /// Date the channel reported the payment as returned (closed IBAN,
    /// uncollected postal order, ...). Populated when <see cref="Status"/>
    /// reaches <see cref="BenefitPaymentStatus.Returned"/>.
    /// </summary>
    public DateOnly? ReturnedDate { get; set; }

    /// <summary>
    /// Human-readable reason captured alongside
    /// <see cref="BenefitPaymentStatus.Returned"/> or
    /// <see cref="BenefitPaymentStatus.Cancelled"/>. <c>null</c> on healthy
    /// (Scheduled / Issued / Paid) rows.
    /// </summary>
    public string? ReturnReason { get; set; }
}
