using Cnas.Ps.Contracts.Interop;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0634 / TOR CF 14.12 / Annex 4 — validates the IDNP-only interop request
/// envelopes (<c>GetInsuredPersonStatus</c>, <c>GetBenefitsList</c>,
/// <c>GetPersonalAccountSnapshot</c>). Strict 13-digit-numeric format check
/// at the API boundary; deeper IDNP-specific validation (mod-10 checksum,
/// century-digit) lives inside <c>Cnas.Ps.Core.ValueObjects.Idnp.TryCreate</c>
/// and is invoked at the service boundary so a single failure code surfaces.
/// </summary>
public sealed class InteropIdnpRequestDtoValidator : AbstractValidator<InteropIdnpRequestDto>
{
    /// <summary>Required IDNP length — Moldovan IDNPs are always 13 characters.</summary>
    public const int IdnpLength = 13;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public InteropIdnpRequestDtoValidator()
    {
        RuleFor(x => x.Idnp)
            .NotEmpty()
            .WithMessage("IDNP is required.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.Length == IdnpLength)
            .WithMessage($"IDNP must be exactly {IdnpLength} characters long.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.All(char.IsDigit))
            .WithMessage("IDNP must contain digit characters only.");
    }
}

/// <summary>
/// R0634 / TOR CF 14.12 / Annex 4 — validates the
/// <see cref="InteropContributionHistoryRequestDto"/> envelope. Enforces the
/// IDNP shape rules of
/// <see cref="InteropIdnpRequestDtoValidator"/> plus the
/// <c>FromMonth ≤ ToMonth</c> ordering and the
/// <see cref="MaxWindowMonths"/> window cap. The window cap is generous
/// enough to cover the standard rolling 5-year contribution-stagiu window
/// (60 months) and tight enough to keep an inter-system pull bounded.
/// </summary>
public sealed class InteropContributionHistoryRequestValidator
    : AbstractValidator<InteropContributionHistoryRequestDto>
{
    /// <summary>Maximum permitted total window size in months (inclusive).</summary>
    public const int MaxWindowMonths = 60;

    /// <summary>Required IDNP length — mirrors <see cref="InteropIdnpRequestDtoValidator.IdnpLength"/>.</summary>
    public const int IdnpLength = InteropIdnpRequestDtoValidator.IdnpLength;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public InteropContributionHistoryRequestValidator()
    {
        RuleFor(x => x.Idnp)
            .NotEmpty()
            .WithMessage("IDNP is required.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.Length == IdnpLength)
            .WithMessage($"IDNP must be exactly {IdnpLength} characters long.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.All(char.IsDigit))
            .WithMessage("IDNP must contain digit characters only.");

        RuleFor(x => x)
            .Must(q => q.FromMonth <= q.ToMonth)
            .WithMessage("FromMonth must be on or before ToMonth.");
        RuleFor(x => x)
            .Must(q => ComputeMonthsInclusive(q.FromMonth, q.ToMonth) <= MaxWindowMonths)
            .WithMessage($"Window must not exceed {MaxWindowMonths} months.");
    }

    /// <summary>
    /// Counts the inclusive number of calendar months covered by the
    /// supplied range. <c>(2025-01, 2025-01)</c> returns 1;
    /// <c>(2021-01, 2025-12)</c> returns 60. Mirrors the arithmetic helper
    /// used by <c>BenefitPaymentStatusQueryDtoValidator</c>.
    /// </summary>
    /// <param name="from">Inclusive lower bound (day component ignored).</param>
    /// <param name="to">Inclusive upper bound (day component ignored).</param>
    /// <returns>Count of inclusive calendar months covered by <c>[from, to]</c>.</returns>
    public static int ComputeMonthsInclusive(DateOnly from, DateOnly to)
        => ((to.Year - from.Year) * 12) + (to.Month - from.Month) + 1;
}

/// <summary>
/// R1702 / TOR CF 14.12 / Annex 4 — validates the
/// <see cref="ActiveDecisionsRequestDto"/> envelope. Same shape and rules as
/// <see cref="InteropIdnpRequestDtoValidator"/>; the type is kept distinct
/// so the FluentValidation pipeline can wire a per-op validator without
/// sharing rules across unrelated request envelopes.
/// </summary>
public sealed class ActiveDecisionsRequestDtoValidator : AbstractValidator<ActiveDecisionsRequestDto>
{
    /// <summary>Required IDNP length — mirrors <see cref="InteropIdnpRequestDtoValidator.IdnpLength"/>.</summary>
    public const int IdnpLength = InteropIdnpRequestDtoValidator.IdnpLength;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public ActiveDecisionsRequestDtoValidator()
    {
        RuleFor(x => x.Idnp).NotEmpty().WithMessage("IDNP is required.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.Length == IdnpLength)
            .WithMessage($"IDNP must be exactly {IdnpLength} characters long.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.All(char.IsDigit))
            .WithMessage("IDNP must contain digit characters only.");
    }
}

/// <summary>
/// R1703 / TOR CF 14.12 / Annex 4 — validates the
/// <see cref="PaymentStatusRequestDto"/> envelope. Enforces a non-empty
/// Sqid handle (deeper Sqid-shape validation happens in the
/// <c>ISqidService.Decode</c> path inside the service implementation) and
/// a sane reporting-month window.
/// </summary>
public sealed class PaymentStatusRequestDtoValidator : AbstractValidator<PaymentStatusRequestDto>
{
    /// <summary>Minimum supported reporting year — pre-1950 windows are rejected as nonsensical.</summary>
    public const int MinSupportedYear = 1950;

    /// <summary>Maximum supported reporting year — values above this surface as out-of-range.</summary>
    public const int MaxSupportedYear = 2099;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public PaymentStatusRequestDtoValidator()
    {
        RuleFor(x => x.DecisionSqid).NotEmpty().WithMessage("DecisionSqid is required.");
        RuleFor(x => x.DecisionSqid)
            .Must(s => s is not null && s.Length is >= 1 and <= 64)
            .WithMessage("DecisionSqid length is out of range.");
        RuleFor(x => x.Period)
            .Must(p => p.Year is >= MinSupportedYear and <= MaxSupportedYear)
            .WithMessage($"Period year must be in [{MinSupportedYear}, {MaxSupportedYear}].");
    }
}

/// <summary>
/// R1704 / TOR CF 14.12 / Annex 4 — validates the
/// <see cref="PayerDataRequestDto"/> envelope. The taxpayer code must be a
/// 13-digit numeric string; the precise dispatch (IDNP vs. IDNO) happens
/// later in the service.
/// </summary>
public sealed class PayerDataRequestDtoValidator : AbstractValidator<PayerDataRequestDto>
{
    /// <summary>Required code length — both IDNP and IDNO are 13 digits.</summary>
    public const int CodeLength = 13;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public PayerDataRequestDtoValidator()
    {
        RuleFor(x => x.TaxpayerCode).NotEmpty().WithMessage("TaxpayerCode is required.");
        RuleFor(x => x.TaxpayerCode)
            .Must(s => s is not null && s.Length == CodeLength)
            .WithMessage($"TaxpayerCode must be exactly {CodeLength} characters long.");
        RuleFor(x => x.TaxpayerCode)
            .Must(s => s is not null && s.All(char.IsDigit))
            .WithMessage("TaxpayerCode must contain digit characters only.");
    }
}

/// <summary>
/// R1705 / TOR CF 14.12 / Annex 4 — validates the
/// <see cref="IsBenefitBeneficiaryRequestDto"/> envelope. Enforces the IDNP
/// shape rules plus a non-empty benefit-type string. Deeper benefit-type
/// validation (parsing into the <c>BenefitType</c> enum) happens at the
/// service boundary so a single failure code surfaces.
/// </summary>
public sealed class IsBenefitBeneficiaryRequestDtoValidator
    : AbstractValidator<IsBenefitBeneficiaryRequestDto>
{
    /// <summary>Required IDNP length — mirrors <see cref="InteropIdnpRequestDtoValidator.IdnpLength"/>.</summary>
    public const int IdnpLength = InteropIdnpRequestDtoValidator.IdnpLength;

    /// <summary>Maximum benefit-type string length — generous cap to forbid pathological inputs.</summary>
    public const int MaxBenefitTypeLength = 64;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public IsBenefitBeneficiaryRequestDtoValidator()
    {
        RuleFor(x => x.Idnp).NotEmpty().WithMessage("IDNP is required.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.Length == IdnpLength)
            .WithMessage($"IDNP must be exactly {IdnpLength} characters long.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.All(char.IsDigit))
            .WithMessage("IDNP must contain digit characters only.");

        RuleFor(x => x.BenefitType).NotEmpty().WithMessage("BenefitType is required.");
        RuleFor(x => x.BenefitType)
            .Must(s => s is not null && s.Length <= MaxBenefitTypeLength)
            .WithMessage($"BenefitType length must be {MaxBenefitTypeLength} characters or fewer.");
    }
}

/// <summary>
/// R1706 / TOR CF 14.12 / Annex 4 — validates the
/// <see cref="ContributionPaymentInfoRequestDto"/> envelope. Enforces the
/// IDNO shape (13-digit numeric) plus a sane reporting-month window.
/// </summary>
public sealed class ContributionPaymentInfoRequestDtoValidator
    : AbstractValidator<ContributionPaymentInfoRequestDto>
{
    /// <summary>Required IDNO length — Moldovan IDNOs are always 13 characters.</summary>
    public const int IdnoLength = 13;

    /// <summary>Minimum supported reporting year — mirrors <see cref="PaymentStatusRequestDtoValidator.MinSupportedYear"/>.</summary>
    public const int MinSupportedYear = PaymentStatusRequestDtoValidator.MinSupportedYear;

    /// <summary>Maximum supported reporting year — mirrors <see cref="PaymentStatusRequestDtoValidator.MaxSupportedYear"/>.</summary>
    public const int MaxSupportedYear = PaymentStatusRequestDtoValidator.MaxSupportedYear;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public ContributionPaymentInfoRequestDtoValidator()
    {
        RuleFor(x => x.Idno).NotEmpty().WithMessage("IDNO is required.");
        RuleFor(x => x.Idno)
            .Must(s => s is not null && s.Length == IdnoLength)
            .WithMessage($"IDNO must be exactly {IdnoLength} characters long.");
        RuleFor(x => x.Idno)
            .Must(s => s is not null && s.All(char.IsDigit))
            .WithMessage("IDNO must contain digit characters only.");
        RuleFor(x => x.Period)
            .Must(p => p.Year is >= MinSupportedYear and <= MaxSupportedYear)
            .WithMessage($"Period year must be in [{MinSupportedYear}, {MaxSupportedYear}].");
    }
}

/// <summary>
/// R1707 / TOR CF 14.12 / Annex 4 — validates the
/// <see cref="LegalApplicableFormRequestDto"/> envelope. Enforces the IDNP
/// shape plus a stable bilateral-agreement code shape
/// (uppercase letters / digits / underscores, 6-32 characters).
/// </summary>
public sealed class LegalApplicableFormRequestDtoValidator
    : AbstractValidator<LegalApplicableFormRequestDto>
{
    /// <summary>Required IDNP length — mirrors <see cref="InteropIdnpRequestDtoValidator.IdnpLength"/>.</summary>
    public const int IdnpLength = InteropIdnpRequestDtoValidator.IdnpLength;

    /// <summary>Minimum agreement-code length (e.g. <c>RO_MD_06</c>).</summary>
    public const int MinAgreementCodeLength = 6;

    /// <summary>Maximum agreement-code length — generous cap to forbid pathological inputs.</summary>
    public const int MaxAgreementCodeLength = 32;

    /// <summary>Creates the validator with all field rules in place.</summary>
    public LegalApplicableFormRequestDtoValidator()
    {
        RuleFor(x => x.Idnp).NotEmpty().WithMessage("IDNP is required.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.Length == IdnpLength)
            .WithMessage($"IDNP must be exactly {IdnpLength} characters long.");
        RuleFor(x => x.Idnp)
            .Must(s => s is not null && s.All(char.IsDigit))
            .WithMessage("IDNP must contain digit characters only.");

        RuleFor(x => x.AgreementCode).NotEmpty().WithMessage("AgreementCode is required.");
        RuleFor(x => x.AgreementCode)
            .Must(s => s is not null
                      && s.Length >= MinAgreementCodeLength
                      && s.Length <= MaxAgreementCodeLength)
            .WithMessage(
                $"AgreementCode length must be in [{MinAgreementCodeLength}, {MaxAgreementCodeLength}].");
        RuleFor(x => x.AgreementCode)
            .Must(s => s is not null && s.All(c => char.IsLetterOrDigit(c) || c == '_'))
            .WithMessage("AgreementCode may contain letters, digits, and underscores only.");
    }
}
