using System;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>R2506 / TOR PIR 037-040 — validates <see cref="QualityRiskCreateInputDto"/>.</summary>
public sealed class QualityRiskCreateInputValidator : AbstractValidator<QualityRiskCreateInputDto>
{
    /// <summary>Stable RiskCode regex — SCREAMING_SNAKE_CASE, ≤ 64 chars.</summary>
    public const string CodeRegex = "^[A-Z][A-Z0-9_]{1,63}$";

    /// <summary>Compiled <see cref="CodeRegex"/> instance.</summary>
    private static readonly Regex CompiledCode = new(
        CodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public QualityRiskCreateInputValidator()
    {
        RuleFor(x => x.RiskCode)
            .NotEmpty().WithMessage("RiskCode is required.")
            .MaximumLength(64).WithMessage("RiskCode must be 64 characters or fewer.")
            .Must(s => s is not null && CompiledCode.IsMatch(s))
            .WithMessage("RiskCode must match the SCREAMING_SNAKE_CASE pattern.");

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MinimumLength(3).WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MinimumLength(50).WithMessage("Description must be 50 characters or more.")
            .MaximumLength(4000).WithMessage("Description must be 4000 characters or fewer.");

        RuleFor(x => x.Category)
            .NotEmpty().WithMessage("Category is required.")
            .Must(s => Enum.TryParse<QualityRiskCategory>(s, ignoreCase: false, out _))
            .WithMessage("Category must be a stable QualityRiskCategory enum-name.");

        RuleFor(x => x.Likelihood)
            .NotEmpty().WithMessage("Likelihood is required.")
            .Must(s => Enum.TryParse<QualityRiskLikelihood>(s, ignoreCase: false, out _))
            .WithMessage("Likelihood must be a stable QualityRiskLikelihood enum-name.");

        RuleFor(x => x.Impact)
            .NotEmpty().WithMessage("Impact is required.")
            .Must(s => Enum.TryParse<QualityRiskImpact>(s, ignoreCase: false, out _))
            .WithMessage("Impact must be a stable QualityRiskImpact enum-name.");

        RuleFor(x => x.OwnerSqid)
            .NotEmpty().WithMessage("OwnerSqid is required.");
    }
}

/// <summary>R2506 / TOR PIR 037-040 — validates <see cref="QualityRiskModifyInputDto"/>.</summary>
public sealed class QualityRiskModifyInputValidator : AbstractValidator<QualityRiskModifyInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public QualityRiskModifyInputValidator()
    {
        RuleFor(x => x.Title)
            .MinimumLength(3).When(x => x.Title is not null)
            .WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).When(x => x.Title is not null)
            .WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MinimumLength(50).When(x => x.Description is not null)
            .WithMessage("Description must be 50 characters or more.")
            .MaximumLength(4000).When(x => x.Description is not null)
            .WithMessage("Description must be 4000 characters or fewer.");

        RuleFor(x => x.Category)
            .Must(s => s is null
                || Enum.TryParse<QualityRiskCategory>(s, ignoreCase: false, out _))
            .WithMessage("Category must be a stable QualityRiskCategory enum-name when supplied.");

        RuleFor(x => x.Likelihood)
            .Must(s => s is null
                || Enum.TryParse<QualityRiskLikelihood>(s, ignoreCase: false, out _))
            .WithMessage("Likelihood must be a stable QualityRiskLikelihood enum-name when supplied.");

        RuleFor(x => x.Impact)
            .Must(s => s is null
                || Enum.TryParse<QualityRiskImpact>(s, ignoreCase: false, out _))
            .WithMessage("Impact must be a stable QualityRiskImpact enum-name when supplied.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(3).WithMessage("ChangeReason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("ChangeReason must be 1000 characters or fewer.");
    }
}

/// <summary>R2506 / TOR PIR 037-040 — validates <see cref="QualityRiskReviewInputDto"/>.</summary>
public sealed class QualityRiskReviewInputValidator : AbstractValidator<QualityRiskReviewInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public QualityRiskReviewInputValidator()
    {
        RuleFor(x => x.ReviewNote)
            .NotEmpty().WithMessage("ReviewNote is required.")
            .MinimumLength(3).WithMessage("ReviewNote must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("ReviewNote must be 1000 characters or fewer.");
    }
}

/// <summary>R2506 / TOR PIR 037-040 — validates <see cref="QualityRiskReasonInputDto"/>.</summary>
public sealed class QualityRiskReasonInputValidator : AbstractValidator<QualityRiskReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public QualityRiskReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("Reason must be 1000 characters or fewer.");
    }
}

/// <summary>R2506 / TOR PIR 037-040 — validates <see cref="QualityRiskFilterDto"/>.</summary>
public sealed class QualityRiskFilterValidator : AbstractValidator<QualityRiskFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public QualityRiskFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<QualityRiskStatus>(s, ignoreCase: false, out _))
            .WithMessage("Status must be a stable QualityRiskStatus enum-name when supplied.");

        RuleFor(x => x.Category)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<QualityRiskCategory>(s, ignoreCase: false, out _))
            .WithMessage("Category must be a stable QualityRiskCategory enum-name when supplied.");

        RuleFor(x => x.Likelihood)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<QualityRiskLikelihood>(s, ignoreCase: false, out _))
            .WithMessage("Likelihood must be a stable QualityRiskLikelihood enum-name when supplied.");

        RuleFor(x => x.Impact)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<QualityRiskImpact>(s, ignoreCase: false, out _))
            .WithMessage("Impact must be a stable QualityRiskImpact enum-name when supplied.");
    }
}

/// <summary>R2506 / TOR PIR 037-040 — validates <see cref="QualityRiskActionCreateInputDto"/>.</summary>
public sealed class QualityRiskActionCreateInputValidator : AbstractValidator<QualityRiskActionCreateInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public QualityRiskActionCreateInputValidator()
    {
        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MinimumLength(3).WithMessage("Description must be 3 characters or more.")
            .MaximumLength(2000).WithMessage("Description must be 2000 characters or fewer.");

        RuleFor(x => x.AssignedToSqid)
            .NotEmpty().WithMessage("AssignedToSqid is required.");
    }
}

/// <summary>R2506 / TOR PIR 037-040 — validates <see cref="QualityRiskActionModifyInputDto"/>.</summary>
public sealed class QualityRiskActionModifyInputValidator : AbstractValidator<QualityRiskActionModifyInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public QualityRiskActionModifyInputValidator()
    {
        RuleFor(x => x.Description)
            .MinimumLength(3).When(x => x.Description is not null)
            .WithMessage("Description must be 3 characters or more.")
            .MaximumLength(2000).When(x => x.Description is not null)
            .WithMessage("Description must be 2000 characters or fewer.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(3).WithMessage("ChangeReason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("ChangeReason must be 1000 characters or fewer.");
    }
}

/// <summary>R2506 / TOR PIR 037-040 — validates <see cref="QualityRiskActionImplementInputDto"/>.</summary>
public sealed class QualityRiskActionImplementInputValidator : AbstractValidator<QualityRiskActionImplementInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public QualityRiskActionImplementInputValidator()
    {
        RuleFor(x => x.CompletionNote)
            .NotEmpty().WithMessage("CompletionNote is required.")
            .MinimumLength(3).WithMessage("CompletionNote must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("CompletionNote must be 1000 characters or fewer.");
    }
}

/// <summary>R2506 / TOR PIR 037-040 — validates <see cref="QualityRiskActionReasonInputDto"/>.</summary>
public sealed class QualityRiskActionReasonInputValidator : AbstractValidator<QualityRiskActionReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public QualityRiskActionReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");
    }
}
