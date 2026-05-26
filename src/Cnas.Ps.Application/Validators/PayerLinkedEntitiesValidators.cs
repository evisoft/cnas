using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0301 — validator for <see cref="PayerAddressInputDto"/>. Enforces field lengths,
/// postal-code shape (4..10 alphanumeric), and ISO-3166-1 alpha-2 country format.
/// </summary>
public sealed class PayerAddressInputDtoValidator : AbstractValidator<PayerAddressInputDto>
{
    /// <summary>ISO-3166-1 alpha-2 — exactly two uppercase letters (e.g. <c>MD</c>, <c>RO</c>).</summary>
    private static readonly Regex CountryPattern =
        new("^[A-Z]{2}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Postal code shape: 4..10 alphanumeric characters.</summary>
    private static readonly Regex PostalCodePattern =
        new("^[A-Za-z0-9]{4,10}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Constructs the validator with field rules.</summary>
    public PayerAddressInputDtoValidator()
    {
        RuleFor(x => x.Street).NotEmpty().Length(1, 200);
        RuleFor(x => x.City).NotEmpty().Length(1, 200);
        RuleFor(x => x.Region).NotEmpty().Length(1, 200);
        RuleFor(x => x.PostalCode)
            .NotEmpty()
            .Must(v => v is not null && PostalCodePattern.IsMatch(v))
            .WithMessage("PostalCode must be 4..10 alphanumeric characters.");
        RuleFor(x => x.Country)
            .NotEmpty()
            .Must(v => v is not null && CountryPattern.IsMatch(v))
            .WithMessage("Country must be a two-letter ISO-3166-1 alpha-2 code (e.g. MD).");
    }
}

/// <summary>R0301 — validator for <see cref="PayerContactInputDto"/>.</summary>
public sealed class PayerContactInputDtoValidator : AbstractValidator<PayerContactInputDto>
{
    /// <summary>E.164 phone shape: leading + then 2..15 digits.</summary>
    private static readonly Regex PhonePattern =
        new(@"^\+\d{2,15}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>RFC 5322-ish simplified email shape (matches FluentValidation's email rule).</summary>
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Constructs the validator.</summary>
    public PayerContactInputDtoValidator()
    {
        RuleFor(x => x.PhoneE164)
            .Must(v => v is null || PhonePattern.IsMatch(v))
            .WithMessage("PhoneE164 must be E.164 (e.g. +37322000000).");
        RuleFor(x => x.Email)
            .Must(v => v is null || EmailPattern.IsMatch(v))
            .WithMessage("Email must be a valid email address.");
        RuleFor(x => x.ContactPersonName)
            .MaximumLength(200);
    }
}

/// <summary>R0301 — validator for <see cref="PayerActivityCaemInputDto"/>.</summary>
public sealed class PayerActivityCaemInputDtoValidator : AbstractValidator<PayerActivityCaemInputDto>
{
    /// <summary>CAEM Rev. 2 canonical form: <c>X.YY.ZZ</c> (e.g. <c>M.69.10</c>).</summary>
    private static readonly Regex CaemPattern =
        new(@"^[A-Z]\.\d{2}\.\d{2}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Constructs the validator.</summary>
    public PayerActivityCaemInputDtoValidator()
    {
        RuleFor(x => x.CaemCode)
            .NotEmpty()
            .Must(v => v is not null && CaemPattern.IsMatch(v))
            .WithMessage("CaemCode must match the CAEM Rev. 2 pattern X.YY.ZZ (e.g. M.69.10).");
        RuleFor(x => x.CaemDescription)
            .NotEmpty()
            .Length(1, 500);
    }
}

/// <summary>
/// R0803 — validator for <see cref="PayerBankAccountInputDto"/>. Enforces the
/// canonical (uppercase, de-spaced) ISO 13616 IBAN shape, the BIC/SWIFT 8-or-11
/// shape, and the ISO 4217 alpha-3 currency code. Lower-case IBANs and embedded
/// spaces are tolerated by the canonicaliser inside <see cref="IbanPattern"/> —
/// the service layer uppercases and strips spaces before persisting, but lower-
/// case input is accepted at the validator boundary for ergonomics.
/// </summary>
public sealed class PayerBankAccountInputDtoValidator : AbstractValidator<PayerBankAccountInputDto>
{
    /// <summary>
    /// ISO 13616 IBAN shape after canonicalisation: country letters, two check
    /// digits, then 1..30 alphanumeric chars, total length 4..34. We validate the
    /// canonicalised form (uppercased, spaces stripped) so that callers can submit
    /// "md24 ag00 ..." or "MD24AG00..." interchangeably.
    /// </summary>
    private static readonly Regex IbanPattern =
        new(@"^[A-Z]{2}\d{2}[A-Z0-9]{1,30}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>BIC / SWIFT shape: 6 letters, 2 alnum (location), optional 3 alnum (branch).</summary>
    private static readonly Regex BicPattern =
        new(@"^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>ISO 4217 alpha-3 currency code (e.g. MDL, EUR, USD).</summary>
    private static readonly Regex CurrencyPattern =
        new("^[A-Z]{3}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Constructs the validator with field rules.</summary>
    public PayerBankAccountInputDtoValidator()
    {
        RuleFor(x => x.AccountHolderName)
            .NotEmpty()
            .Length(1, 200);
        RuleFor(x => x.Iban)
            .NotEmpty()
            .Must(v => v is not null && IbanPattern.IsMatch(CanonicaliseIban(v)))
            .WithMessage("Iban must be a valid ISO 13616 IBAN (country letters + check digits + 1..30 alnum, max 34 chars).");
        RuleFor(x => x.BankName)
            .NotEmpty()
            .Length(1, 200);
        RuleFor(x => x.BankBic)
            .NotEmpty()
            .Must(v => v is not null && BicPattern.IsMatch(v))
            .WithMessage("BankBic must be an 8- or 11-character uppercase BIC (e.g. AGRNMD2X or AGRNMD2X123).");
        RuleFor(x => x.Currency)
            .NotEmpty()
            .Must(v => v is not null && CurrencyPattern.IsMatch(v))
            .WithMessage("Currency must be an ISO 4217 alpha-3 code (e.g. MDL).");
    }

    /// <summary>Strips embedded whitespace and uppercases — the canonical form used for validation + hashing.</summary>
    /// <param name="raw">Caller-supplied IBAN, possibly lowercase / containing spaces.</param>
    /// <returns>Uppercased, de-spaced form suitable for ISO 13616 regex validation.</returns>
    public static string CanonicaliseIban(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (!char.IsWhiteSpace(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// R0803 — validator for <see cref="PayerSecondaryContactInputDto"/>. Reuses the
/// E.164 + email shapes from <see cref="PayerContactInputDtoValidator"/> and
/// enforces the 100-char Role + 200-char ContactPersonName limits.
/// </summary>
public sealed class PayerSecondaryContactInputDtoValidator : AbstractValidator<PayerSecondaryContactInputDto>
{
    /// <summary>E.164 phone shape: leading + then 2..15 digits.</summary>
    private static readonly Regex PhonePattern =
        new(@"^\+\d{2,15}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Simplified email shape (matches FluentValidation's default).</summary>
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Constructs the validator.</summary>
    public PayerSecondaryContactInputDtoValidator()
    {
        RuleFor(x => x.ContactPersonName)
            .NotEmpty()
            .Length(1, 200);
        RuleFor(x => x.Role)
            .MaximumLength(100);
        RuleFor(x => x.PhoneE164)
            .Must(v => v is null || PhonePattern.IsMatch(v))
            .WithMessage("PhoneE164 must be E.164 (e.g. +37322000000).");
        RuleFor(x => x.Email)
            .Must(v => v is null || EmailPattern.IsMatch(v))
            .WithMessage("Email must be a valid email address.");
    }
}
