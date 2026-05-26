using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>R0311 — validator for <see cref="ContributorAddressInputDto"/>.</summary>
public sealed class ContributorAddressInputDtoValidator : AbstractValidator<ContributorAddressInputDto>
{
    /// <summary>ISO-3166-1 alpha-2 country pattern.</summary>
    private static readonly Regex CountryPattern =
        new("^[A-Z]{2}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Postal-code shape (4..10 alphanumeric).</summary>
    private static readonly Regex PostalCodePattern =
        new("^[A-Za-z0-9]{4,10}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Constructs the validator.</summary>
    public ContributorAddressInputDtoValidator()
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

/// <summary>R0311 — validator for <see cref="ContributorContactInputDto"/>.</summary>
public sealed class ContributorContactInputDtoValidator : AbstractValidator<ContributorContactInputDto>
{
    /// <summary>E.164 phone pattern.</summary>
    private static readonly Regex PhonePattern =
        new(@"^\+\d{2,15}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Email shape.</summary>
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Constructs the validator.</summary>
    public ContributorContactInputDtoValidator()
    {
        RuleFor(x => x.PhoneE164)
            .Must(v => v is null || PhonePattern.IsMatch(v))
            .WithMessage("PhoneE164 must be E.164.");
        RuleFor(x => x.Email)
            .Must(v => v is null || EmailPattern.IsMatch(v))
            .WithMessage("Email must be a valid email address.");
        RuleFor(x => x.ContactPersonName).MaximumLength(200);
    }
}

/// <summary>R0311 — validator for <see cref="ContributorActivityPeriodInputDto"/>.</summary>
public sealed class ContributorActivityPeriodInputDtoValidator : AbstractValidator<ContributorActivityPeriodInputDto>
{
    /// <summary>Constructs the validator.</summary>
    public ContributorActivityPeriodInputDtoValidator()
    {
        RuleFor(x => x.EmployerCode).NotEmpty().Length(1, 64);
        RuleFor(x => x.Position).NotEmpty().Length(1, 200);
        RuleFor(x => x.MonthlySalary)
            .Must(v => v is null || (v >= 0m && v <= 1_000_000m))
            .WithMessage("MonthlySalary must be between 0 and 1,000,000 when supplied.");
    }
}

/// <summary>R0311 — validator for <see cref="ContributorCivilStatusInputDto"/>.</summary>
public sealed class ContributorCivilStatusInputDtoValidator : AbstractValidator<ContributorCivilStatusInputDto>
{
    /// <summary>Permitted civil-status string values (mirror <c>CivilStatusType</c>).</summary>
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Single", "Married", "Divorced", "Widowed", "Separated",
    };

    /// <summary>Constructs the validator.</summary>
    public ContributorCivilStatusInputDtoValidator()
    {
        RuleFor(x => x.Status)
            .NotEmpty()
            .Must(v => v is not null && ValidStatuses.Contains(v))
            .WithMessage($"Status must be one of: {string.Join(", ", ValidStatuses)}.");
    }
}

/// <summary>R0311 — validator for <see cref="ContributorSocialInsuranceContractInputDto"/>.</summary>
public sealed class ContributorSocialInsuranceContractInputDtoValidator
    : AbstractValidator<ContributorSocialInsuranceContractInputDto>
{
    /// <summary>Constructs the validator.</summary>
    public ContributorSocialInsuranceContractInputDtoValidator()
    {
        RuleFor(x => x.ContractNumber).NotEmpty().Length(1, 50);
        RuleFor(x => x.MonthlyContributionAmount)
            .InclusiveBetween(0m, 1_000_000m);
        RuleFor(x => x.CounterpartyName).MaximumLength(200);
        // Cross-field: when end date is supplied it must be strictly after the start date.
        RuleFor(x => x)
            .Must(x => x.ContractEndDate is null || x.ContractEndDate > x.ContractStartDate)
            .WithMessage("ContractEndDate must be strictly after ContractStartDate when supplied.");
    }
}

/// <summary>R0311 — validator for <see cref="ContributorPre1999PeriodCarnetMuncaInputDto"/>.</summary>
public sealed class ContributorPre1999PeriodCarnetMuncaInputDtoValidator
    : AbstractValidator<ContributorPre1999PeriodCarnetMuncaInputDto>
{
    /// <summary>Constructs the validator.</summary>
    public ContributorPre1999PeriodCarnetMuncaInputDtoValidator()
    {
        RuleFor(x => x.CarnetMuncaNumber).NotEmpty().Length(1, 50);
        RuleFor(x => x.EmployerName).MaximumLength(200);
        RuleFor(x => x.Position).MaximumLength(200);
        RuleFor(x => x)
            .Must(x => x.PeriodEndDate >= x.PeriodStartDate)
            .WithMessage("PeriodEndDate must be on or after PeriodStartDate.");
    }
}
