using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0127 / CF 16.11 — validator for <see cref="WorkflowTaskReassignDto"/>. Rejects empty
/// or trivially-short reasons; the service layer enforces the rest of the contract
/// (target user exists, task not terminal, etc.).
/// </summary>
public sealed class WorkflowTaskReassignDtoValidator : AbstractValidator<WorkflowTaskReassignDto>
{
    /// <summary>
    /// Minimum length of the reason string. A 3-char floor blocks the trivial
    /// "x" / empty cases without forcing a verbose justification.
    /// </summary>
    public const int MinReasonLength = 3;

    /// <summary>
    /// Maximum length of the reason string. Mirrors the
    /// <c>WorkflowTask.ReassignmentReason</c> column cap so the validator never lets a
    /// row reach the service layer that would later be truncated by the EF column
    /// mapping.
    /// </summary>
    public const int MaxReasonLength = 500;

    /// <summary>Wires the rule set.</summary>
    public WorkflowTaskReassignDtoValidator()
    {
        RuleFor(x => x.NewAssigneeSqid)
            .NotEmpty()
            .WithMessage("NewAssigneeSqid is required.");

        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(MinReasonLength)
            .MaximumLength(MaxReasonLength)
            .WithMessage($"Reason must be {MinReasonLength}..{MaxReasonLength} chars.");
    }
}

/// <summary>
/// R0127 / CF 16.11 — validator for <see cref="UserAbsenceCreateDto"/>. Enforces:
/// <list type="bullet">
///   <item>Reason length: 3..200 chars (matches the column cap).</item>
///   <item>End ≥ Start.</item>
///   <item>Max duration 365 days (defensive — an absence longer than a year is almost
///   certainly an operator typo, not a legitimate request).</item>
///   <item>Start no earlier than today − 7 days. Backdating an absence further than a
///   week rewrites the audit trail retroactively (the tasks routed when the absence
///   activates would carry today's timestamp even though the operator is claiming
///   they "started" months ago).</item>
///   <item>User ≠ Delegate.</item>
/// </list>
/// </summary>
public sealed class UserAbsenceCreateDtoValidator : AbstractValidator<UserAbsenceCreateDto>
{
    /// <summary>Minimum length of the reason string.</summary>
    public const int MinReasonLength = 3;

    /// <summary>Maximum length of the reason string — mirrors the column cap.</summary>
    public const int MaxReasonLength = 200;

    /// <summary>
    /// Max absence duration in days. A year is the practical upper bound for staff
    /// absences in the CNAS context (maternity leave + extension); longer windows are
    /// rejected as operator errors.
    /// </summary>
    public const int MaxDurationDays = 365;

    /// <summary>
    /// Maximum backdating allowed for the start date in days. Beyond this window, the
    /// validator refuses the plan so operators cannot retroactively rewrite the
    /// audit trail by activating an absence "as of" an old start date.
    /// </summary>
    public const int MaxBackdateDays = 7;

    /// <summary>Wires the rule set against an injected <see cref="ICnasTimeProvider"/>.</summary>
    /// <param name="clock">UTC clock — used to compute the backdate cutoff.</param>
    public UserAbsenceCreateDtoValidator(ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var backdateCutoff = clock.UtcNow.Date.AddDays(-MaxBackdateDays);

        RuleFor(x => x.UserSqid)
            .NotEmpty().WithMessage("UserSqid is required.");

        RuleFor(x => x.DelegateSqid)
            .NotEmpty().WithMessage("DelegateSqid is required.");

        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(MinReasonLength)
            .MaximumLength(MaxReasonLength)
            .WithMessage($"Reason must be {MinReasonLength}..{MaxReasonLength} chars.");

        // User != Delegate: an absence cannot delegate the user's own tasks back to
        // the user themselves. Compared via Sqid string equality because the validator
        // runs BEFORE Sqid decoding.
        RuleFor(x => x)
            .Must(input => !string.Equals(input.UserSqid, input.DelegateSqid, StringComparison.Ordinal))
            .WithName(nameof(UserAbsenceCreateDto.DelegateSqid))
            .WithMessage("Delegate must differ from the absent user.");

        // End ≥ Start.
        RuleFor(x => x)
            .Must(input => input.EndDateUtc >= input.StartDateUtc)
            .WithName(nameof(UserAbsenceCreateDto.EndDateUtc))
            .WithMessage("EndDateUtc must be greater than or equal to StartDateUtc.");

        // Max duration.
        RuleFor(x => x)
            .Must(input => (input.EndDateUtc - input.StartDateUtc).TotalDays <= MaxDurationDays)
            .WithName(nameof(UserAbsenceCreateDto.EndDateUtc))
            .WithMessage($"Absence duration cannot exceed {MaxDurationDays} days.");

        // Backdating guard — start must be no earlier than today − MaxBackdateDays.
        RuleFor(x => x.StartDateUtc)
            .Must(start => start.Date >= backdateCutoff)
            .WithMessage($"StartDateUtc cannot be backdated more than {MaxBackdateDays} days.");
    }
}
