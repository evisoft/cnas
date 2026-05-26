using System;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>R2505 / TOR PIR 030-033 — validates <see cref="ChangeRequestCreateInputDto"/>.</summary>
public sealed class ChangeRequestCreateInputValidator : AbstractValidator<ChangeRequestCreateInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public ChangeRequestCreateInputValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MinimumLength(3).WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MinimumLength(50).WithMessage("Description must be 50 characters or more.")
            .MaximumLength(8000).WithMessage("Description must be 8000 characters or fewer.");

        RuleFor(x => x.Kind)
            .NotEmpty().WithMessage("Kind is required.")
            .Must(s => Enum.TryParse<ChangeRequestKind>(s, ignoreCase: false, out _))
            .WithMessage("Kind must be a stable ChangeRequestKind enum-name.");

        RuleFor(x => x.Risk)
            .NotEmpty().WithMessage("Risk is required.")
            .Must(s => Enum.TryParse<ChangeRequestRisk>(s, ignoreCase: false, out _))
            .WithMessage("Risk must be a stable ChangeRequestRisk enum-name.");

        RuleFor(x => x.ImpactedSystems)
            .NotEmpty().WithMessage("ImpactedSystems is required.")
            .MinimumLength(3).WithMessage("ImpactedSystems must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("ImpactedSystems must be 1000 characters or fewer.");

        RuleFor(x => x.RollbackPlan)
            .NotEmpty().WithMessage("RollbackPlan is required.")
            .MinimumLength(50).WithMessage("RollbackPlan must be 50 characters or more (non-trivial).")
            .MaximumLength(4000).WithMessage("RollbackPlan must be 4000 characters or fewer.");
    }
}

/// <summary>R2505 / TOR PIR 030-033 — validates <see cref="ChangeRequestTestValidationInputDto"/>.</summary>
public sealed class ChangeRequestTestValidationInputValidator : AbstractValidator<ChangeRequestTestValidationInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public ChangeRequestTestValidationInputValidator()
    {
        RuleFor(x => x.ValidationNote)
            .NotEmpty().WithMessage("ValidationNote is required.")
            .MinimumLength(3).WithMessage("ValidationNote must be 3 characters or more.")
            .MaximumLength(2000).WithMessage("ValidationNote must be 2000 characters or fewer.");
    }
}

/// <summary>R2505 / TOR PIR 030-033 — validates <see cref="ChangeRequestSignCodeInputDto"/>.</summary>
public sealed class ChangeRequestSignCodeInputValidator : AbstractValidator<ChangeRequestSignCodeInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public ChangeRequestSignCodeInputValidator()
    {
        RuleFor(x => x.CodeSignatureReference)
            .NotEmpty().WithMessage("CodeSignatureReference is required.")
            .MinimumLength(3).WithMessage("CodeSignatureReference must be 3 characters or more.")
            .MaximumLength(128).WithMessage("CodeSignatureReference must be 128 characters or fewer.");
    }
}

/// <summary>R2505 / TOR PIR 030-033 — validates <see cref="ChangeRequestRollbackInputDto"/>.</summary>
public sealed class ChangeRequestRollbackInputValidator : AbstractValidator<ChangeRequestRollbackInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public ChangeRequestRollbackInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(2000).WithMessage("Reason must be 2000 characters or fewer.");
    }
}

/// <summary>R2505 / TOR PIR 030-033 — validates <see cref="ChangeRequestReasonInputDto"/>.</summary>
public sealed class ChangeRequestReasonInputValidator : AbstractValidator<ChangeRequestReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public ChangeRequestReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");
    }
}

/// <summary>R2505 / TOR PIR 030-033 — validates <see cref="ChangeRequestFilterDto"/>.</summary>
public sealed class ChangeRequestFilterValidator : AbstractValidator<ChangeRequestFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public ChangeRequestFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<ChangeRequestStatus>(s, ignoreCase: false, out _))
            .WithMessage("Status must be a stable ChangeRequestStatus enum-name when supplied.");

        RuleFor(x => x.Kind)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<ChangeRequestKind>(s, ignoreCase: false, out _))
            .WithMessage("Kind must be a stable ChangeRequestKind enum-name when supplied.");

        RuleFor(x => x.Risk)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<ChangeRequestRisk>(s, ignoreCase: false, out _))
            .WithMessage("Risk must be a stable ChangeRequestRisk enum-name when supplied.");
    }
}
