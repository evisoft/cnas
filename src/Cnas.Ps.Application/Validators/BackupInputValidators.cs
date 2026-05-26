using System;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Quartz;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2307 / TOR SEC 060 — validates <see cref="BackupPolicyCreateInputDto"/>.
/// Pins the policy-code regex, length bounds, enum membership for Scope /
/// Strategy / TargetKind, and Quartz-cron well-formedness for the
/// CronSchedule field.
/// </summary>
public sealed class BackupPolicyCreateInputValidator : AbstractValidator<BackupPolicyCreateInputDto>
{
    /// <summary>Stable PolicyCode regex — SCREAMING_SNAKE_CASE with optional dots, ≤ 64 chars.</summary>
    public const string PolicyCodeRegex = "^[A-Z][A-Z0-9_.]{1,63}$";

    /// <summary>Compiled <see cref="PolicyCodeRegex"/> instance.</summary>
    private static readonly Regex CompiledPolicyCode = new(
        PolicyCodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Lower bound on RetentionDays.</summary>
    public const int MinRetentionDays = 1;

    /// <summary>Upper bound on RetentionDays (≈ 10 years).</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public BackupPolicyCreateInputValidator()
    {
        RuleFor(x => x.PolicyCode)
            .NotEmpty().WithMessage("PolicyCode is required.")
            .MaximumLength(64).WithMessage("PolicyCode must be 64 characters or fewer.")
            .Must(s => s is not null && CompiledPolicyCode.IsMatch(s))
            .WithMessage("PolicyCode must match the SCREAMING_SNAKE_CASE pattern.");

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("DisplayName is required.")
            .MinimumLength(3).WithMessage("DisplayName must be 3 characters or more.")
            .MaximumLength(256).WithMessage("DisplayName must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description must be 2000 characters or fewer.");

        RuleFor(x => x.Scope)
            .NotEmpty().WithMessage("Scope is required.")
            .Must(IsKnownScope)
            .WithMessage("Scope must be a stable BackupScope enum-name.");

        RuleFor(x => x.Strategy)
            .NotEmpty().WithMessage("Strategy is required.")
            .Must(IsKnownStrategy)
            .WithMessage("Strategy must be a stable BackupStrategy enum-name.");

        RuleFor(x => x.TargetKind)
            .NotEmpty().WithMessage("TargetKind is required.")
            .Must(IsKnownTargetKind)
            .WithMessage("TargetKind must be a stable BackupTargetKind enum-name.");

        RuleFor(x => x.CronSchedule)
            .NotEmpty().WithMessage("CronSchedule is required.")
            .MaximumLength(64).WithMessage("CronSchedule must be 64 characters or fewer.")
            .Must(BeValidQuartzCron)
            .WithMessage("CronSchedule must be a valid Quartz cron expression.");

        RuleFor(x => x.RetentionDays)
            .InclusiveBetween(MinRetentionDays, MaxRetentionDays)
            .WithMessage($"RetentionDays must be in [{MinRetentionDays}, {MaxRetentionDays}].");

        RuleFor(x => x.TargetReference)
            .MaximumLength(256).WithMessage("TargetReference must be 256 characters or fewer.");
    }

    /// <summary>Returns <c>true</c> when <paramref name="scope"/> parses to a <see cref="BackupScope"/>.</summary>
    /// <param name="scope">Candidate scope.</param>
    /// <returns><c>true</c> iff the value is a known enum-name.</returns>
    private static bool IsKnownScope(string? scope)
        => !string.IsNullOrWhiteSpace(scope)
           && Enum.TryParse<BackupScope>(scope, ignoreCase: false, out _);

    /// <summary>Returns <c>true</c> when <paramref name="strategy"/> parses to a <see cref="BackupStrategy"/>.</summary>
    /// <param name="strategy">Candidate strategy.</param>
    /// <returns><c>true</c> iff the value is a known enum-name.</returns>
    private static bool IsKnownStrategy(string? strategy)
        => !string.IsNullOrWhiteSpace(strategy)
           && Enum.TryParse<BackupStrategy>(strategy, ignoreCase: false, out _);

    /// <summary>Returns <c>true</c> when <paramref name="kind"/> parses to a <see cref="BackupTargetKind"/>.</summary>
    /// <param name="kind">Candidate target-kind.</param>
    /// <returns><c>true</c> iff the value is a known enum-name.</returns>
    private static bool IsKnownTargetKind(string? kind)
        => !string.IsNullOrWhiteSpace(kind)
           && Enum.TryParse<BackupTargetKind>(kind, ignoreCase: false, out _);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="cron"/> is a well-formed
    /// Quartz cron expression, per the existing Quartz.NET dependency.
    /// </summary>
    /// <param name="cron">Candidate cron string.</param>
    /// <returns><c>true</c> iff <c>Quartz.CronExpression.IsValidExpression</c> accepts the value.</returns>
    private static bool BeValidQuartzCron(string? cron)
        => !string.IsNullOrWhiteSpace(cron)
           && CronExpression.IsValidExpression(cron);
}

/// <summary>R2307 / TOR SEC 060 — validates <see cref="BackupPolicyModifyInputDto"/>.</summary>
public sealed class BackupPolicyModifyInputValidator : AbstractValidator<BackupPolicyModifyInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public BackupPolicyModifyInputValidator()
    {
        RuleFor(x => x.DisplayName)
            .MinimumLength(3).When(x => x.DisplayName is not null)
            .WithMessage("DisplayName must be 3 characters or more.")
            .MaximumLength(256).When(x => x.DisplayName is not null)
            .WithMessage("DisplayName must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(2000).When(x => x.Description is not null)
            .WithMessage("Description must be 2000 characters or fewer.");

        RuleFor(x => x.CronSchedule)
            .MaximumLength(64).When(x => x.CronSchedule is not null)
            .WithMessage("CronSchedule must be 64 characters or fewer.")
            .Must(s => s is null || CronExpression.IsValidExpression(s))
            .WithMessage("CronSchedule must be a valid Quartz cron expression.");

        RuleFor(x => x.RetentionDays)
            .InclusiveBetween(
                BackupPolicyCreateInputValidator.MinRetentionDays,
                BackupPolicyCreateInputValidator.MaxRetentionDays)
            .When(x => x.RetentionDays is not null)
            .WithMessage($"RetentionDays must be in [{BackupPolicyCreateInputValidator.MinRetentionDays}, {BackupPolicyCreateInputValidator.MaxRetentionDays}].");

        RuleFor(x => x.TargetReference)
            .MaximumLength(256).When(x => x.TargetReference is not null)
            .WithMessage("TargetReference must be 256 characters or fewer.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(3).WithMessage("ChangeReason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("ChangeReason must be 1000 characters or fewer.");
    }
}

/// <summary>R2307 / TOR SEC 060 — validates <see cref="BackupPolicyReasonInputDto"/>.</summary>
public sealed class BackupPolicyReasonInputValidator : AbstractValidator<BackupPolicyReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public BackupPolicyReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("Reason must be 1000 characters or fewer.");
    }
}

/// <summary>R2307 / TOR SEC 060 — validates <see cref="BackupRunFilterDto"/>.</summary>
public sealed class BackupRunFilterValidator : AbstractValidator<BackupRunFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public BackupRunFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<BackupRunStatus>(s, ignoreCase: false, out _))
            .WithMessage("Status must be a stable BackupRunStatus enum-name when supplied.");

        RuleFor(x => x.TriggerKind)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<BackupTriggerKind>(s, ignoreCase: false, out _))
            .WithMessage("TriggerKind must be a stable BackupTriggerKind enum-name when supplied.");
    }
}

/// <summary>R2307 / TOR SEC 060 — validates <see cref="BackupPolicyFilterDto"/>.</summary>
public sealed class BackupPolicyFilterValidator : AbstractValidator<BackupPolicyFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public BackupPolicyFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Scope)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<BackupScope>(s, ignoreCase: false, out _))
            .WithMessage("Scope must be a stable BackupScope enum-name when supplied.");
    }
}
