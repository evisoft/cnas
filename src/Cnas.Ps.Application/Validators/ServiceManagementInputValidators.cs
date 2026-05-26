using System;
using System.Text.RegularExpressions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R2501 / TOR PIR 024 — validates <see cref="BusinessHoursPolicyCreateInputDto"/>.
/// </summary>
public sealed class BusinessHoursPolicyCreateInputValidator : AbstractValidator<BusinessHoursPolicyCreateInputDto>
{
    /// <summary>Stable BusinessHoursPolicy code regex — SCREAMING_SNAKE_CASE, ≤ 64 chars.</summary>
    public const string CodeRegex = "^[A-Z][A-Z0-9_]{1,63}$";

    /// <summary>Compiled <see cref="CodeRegex"/> instance.</summary>
    private static readonly Regex CompiledCode = new(
        CodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Strict HH:mm 24-hour pattern.</summary>
    private static readonly Regex CompiledTime = new(
        "^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public BusinessHoursPolicyCreateInputValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .MaximumLength(64).WithMessage("Code must be 64 characters or fewer.")
            .Must(s => s is not null && CompiledCode.IsMatch(s))
            .WithMessage("Code must match the SCREAMING_SNAKE_CASE pattern.");

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("DisplayName is required.")
            .MinimumLength(3).WithMessage("DisplayName must be 3 characters or more.")
            .MaximumLength(256).WithMessage("DisplayName must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Description must be 1000 characters or fewer.");

        RuleFor(x => x.OpenTimeLocal)
            .NotEmpty().WithMessage("OpenTimeLocal is required.")
            .Must(s => s is not null && CompiledTime.IsMatch(s))
            .WithMessage("OpenTimeLocal must be HH:mm (24-hour).");

        RuleFor(x => x.CloseTimeLocal)
            .NotEmpty().WithMessage("CloseTimeLocal is required.")
            .Must(s => s is not null && CompiledTime.IsMatch(s))
            .WithMessage("CloseTimeLocal must be HH:mm (24-hour).");

        RuleFor(x => x.BusinessDaysMask)
            .InclusiveBetween(1, 127)
            .WithMessage("BusinessDaysMask must be in [1, 127].");

        RuleFor(x => x.TimezoneId)
            .NotEmpty().WithMessage("TimezoneId is required.")
            .MaximumLength(64).WithMessage("TimezoneId must be 64 characters or fewer.");

        RuleFor(x => x.HolidayDatesJson)
            .MaximumLength(8000)
            .WithMessage("HolidayDatesJson must be 8000 characters or fewer.");
    }
}

/// <summary>R2501 / TOR PIR 024 — validates <see cref="BusinessHoursPolicyModifyInputDto"/>.</summary>
public sealed class BusinessHoursPolicyModifyInputValidator : AbstractValidator<BusinessHoursPolicyModifyInputDto>
{
    private static readonly Regex CompiledTime = new(
        "^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public BusinessHoursPolicyModifyInputValidator()
    {
        RuleFor(x => x.DisplayName)
            .MinimumLength(3).When(x => x.DisplayName is not null)
            .WithMessage("DisplayName must be 3 characters or more.")
            .MaximumLength(256).When(x => x.DisplayName is not null)
            .WithMessage("DisplayName must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(1000).When(x => x.Description is not null)
            .WithMessage("Description must be 1000 characters or fewer.");

        RuleFor(x => x.OpenTimeLocal)
            .Must(s => s is null || CompiledTime.IsMatch(s))
            .WithMessage("OpenTimeLocal must be HH:mm (24-hour).");

        RuleFor(x => x.CloseTimeLocal)
            .Must(s => s is null || CompiledTime.IsMatch(s))
            .WithMessage("CloseTimeLocal must be HH:mm (24-hour).");

        RuleFor(x => x.BusinessDaysMask)
            .InclusiveBetween(1, 127).When(x => x.BusinessDaysMask is not null)
            .WithMessage("BusinessDaysMask must be in [1, 127].");

        RuleFor(x => x.TimezoneId)
            .MaximumLength(64).When(x => x.TimezoneId is not null)
            .WithMessage("TimezoneId must be 64 characters or fewer.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(3).WithMessage("ChangeReason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("ChangeReason must be 1000 characters or fewer.");
    }
}

/// <summary>R2501 — validates <see cref="BusinessHoursPolicyReasonInputDto"/>.</summary>
public sealed class BusinessHoursPolicyReasonInputValidator : AbstractValidator<BusinessHoursPolicyReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public BusinessHoursPolicyReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("Reason must be 1000 characters or fewer.");
    }
}

/// <summary>R2501 — validates <see cref="BusinessHoursPolicyFilterDto"/>.</summary>
public sealed class BusinessHoursPolicyFilterValidator : AbstractValidator<BusinessHoursPolicyFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public BusinessHoursPolicyFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");
    }
}

// ─────────────────────────────── R2502 ───────────────────────────────

/// <summary>R2502 — validates <see cref="MaintenanceWindowCreateInputDto"/>.</summary>
/// <remarks>
/// Duration-vs-kind ceilings are enforced at the service level (the validator
/// would otherwise need to know the kind to enforce a different duration).
/// </remarks>
public sealed class MaintenanceWindowCreateInputValidator : AbstractValidator<MaintenanceWindowCreateInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MaintenanceWindowCreateInputValidator()
    {
        RuleFor(x => x.BusinessHoursPolicyCode)
            .NotEmpty().WithMessage("BusinessHoursPolicyCode is required.")
            .MaximumLength(64).WithMessage("BusinessHoursPolicyCode must be 64 characters or fewer.");

        RuleFor(x => x.WindowKind)
            .NotEmpty().WithMessage("WindowKind is required.")
            .Must(IsKnownWindowKind)
            .WithMessage("WindowKind must be a stable MaintenanceWindowKind enum-name.");

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MinimumLength(3).WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.")
            .MinimumLength(3).WithMessage("Description must be 3 characters or more.")
            .MaximumLength(2000).WithMessage("Description must be 2000 characters or fewer.");

        RuleFor(x => x.ScheduledEndUtc)
            .GreaterThan(x => x.ScheduledStartUtc)
            .WithMessage("ScheduledEndUtc must be strictly after ScheduledStartUtc.");
    }

    /// <summary>Returns <c>true</c> when <paramref name="kind"/> is a known <see cref="MaintenanceWindowKind"/>.</summary>
    /// <param name="kind">Candidate enum-name.</param>
    /// <returns><c>true</c> iff the value is a known enum-name.</returns>
    private static bool IsKnownWindowKind(string? kind)
        => !string.IsNullOrWhiteSpace(kind)
           && Enum.TryParse<MaintenanceWindowKind>(kind, ignoreCase: false, out _);
}

/// <summary>R2502 — validates <see cref="MaintenanceWindowReasonInputDto"/>.</summary>
public sealed class MaintenanceWindowReasonInputValidator : AbstractValidator<MaintenanceWindowReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MaintenanceWindowReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");
    }
}

/// <summary>R2502 — validates <see cref="MaintenanceWindowFilterDto"/>.</summary>
public sealed class MaintenanceWindowFilterValidator : AbstractValidator<MaintenanceWindowFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public MaintenanceWindowFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<MaintenanceWindowStatus>(s, ignoreCase: false, out _))
            .WithMessage("Status must be a stable MaintenanceWindowStatus enum-name when supplied.");

        RuleFor(x => x.WindowKind)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<MaintenanceWindowKind>(s, ignoreCase: false, out _))
            .WithMessage("WindowKind must be a stable MaintenanceWindowKind enum-name when supplied.");
    }
}

// ─────────────────────────────── R2503 ───────────────────────────────

/// <summary>R2503 — validates <see cref="SystemUpdateScheduleCreateInputDto"/>.</summary>
public sealed class SystemUpdateScheduleCreateInputValidator : AbstractValidator<SystemUpdateScheduleCreateInputDto>
{
    /// <summary>Stable ScheduleCode regex — SCREAMING_SNAKE_CASE with optional dots, ≤ 64 chars.</summary>
    public const string CodeRegex = "^[A-Z][A-Z0-9_.]{1,63}$";

    /// <summary>Compiled <see cref="CodeRegex"/> instance.</summary>
    private static readonly Regex CompiledCode = new(
        CodeRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Upper bound on NoticeLeadTimeDays (≈ 2 years).</summary>
    public const int MaxNoticeLeadTimeDays = 730;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SystemUpdateScheduleCreateInputValidator()
    {
        RuleFor(x => x.ScheduleCode)
            .NotEmpty().WithMessage("ScheduleCode is required.")
            .MaximumLength(64).WithMessage("ScheduleCode must be 64 characters or fewer.")
            .Must(s => s is not null && CompiledCode.IsMatch(s))
            .WithMessage("ScheduleCode must match the SCREAMING_SNAKE_CASE pattern.");

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MinimumLength(3).WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.Cadence)
            .NotEmpty().WithMessage("Cadence is required.")
            .Must(IsKnownCadence)
            .WithMessage("Cadence must be a stable UpdateCadenceKind enum-name.");

        RuleFor(x => x.NoticeLeadTimeDays)
            .InclusiveBetween(0, MaxNoticeLeadTimeDays)
            .WithMessage($"NoticeLeadTimeDays must be in [0, {MaxNoticeLeadTimeDays}].");

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description must be 2000 characters or fewer.");
    }

    /// <summary>Returns <c>true</c> when <paramref name="cadence"/> is a known <see cref="UpdateCadenceKind"/>.</summary>
    /// <param name="cadence">Candidate enum-name.</param>
    /// <returns><c>true</c> iff the value is a known enum-name.</returns>
    private static bool IsKnownCadence(string? cadence)
        => !string.IsNullOrWhiteSpace(cadence)
           && Enum.TryParse<UpdateCadenceKind>(cadence, ignoreCase: false, out _);
}

/// <summary>R2503 — validates <see cref="SystemUpdateScheduleModifyInputDto"/>.</summary>
public sealed class SystemUpdateScheduleModifyInputValidator : AbstractValidator<SystemUpdateScheduleModifyInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SystemUpdateScheduleModifyInputValidator()
    {
        RuleFor(x => x.Title)
            .MinimumLength(3).When(x => x.Title is not null)
            .WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).When(x => x.Title is not null)
            .WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.NoticeLeadTimeDays)
            .InclusiveBetween(0, SystemUpdateScheduleCreateInputValidator.MaxNoticeLeadTimeDays)
            .When(x => x.NoticeLeadTimeDays is not null)
            .WithMessage($"NoticeLeadTimeDays must be in [0, {SystemUpdateScheduleCreateInputValidator.MaxNoticeLeadTimeDays}].");

        RuleFor(x => x.Description)
            .MaximumLength(2000).When(x => x.Description is not null)
            .WithMessage("Description must be 2000 characters or fewer.");

        RuleFor(x => x.ChangeReason)
            .NotEmpty().WithMessage("ChangeReason is required.")
            .MinimumLength(3).WithMessage("ChangeReason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("ChangeReason must be 1000 characters or fewer.");
    }
}

/// <summary>R2503 — validates <see cref="SystemUpdateScheduleReasonInputDto"/>.</summary>
public sealed class SystemUpdateScheduleReasonInputValidator : AbstractValidator<SystemUpdateScheduleReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SystemUpdateScheduleReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(1000).WithMessage("Reason must be 1000 characters or fewer.");
    }
}

/// <summary>R2503 — validates <see cref="SystemUpdateScheduleFilterDto"/>.</summary>
public sealed class SystemUpdateScheduleFilterValidator : AbstractValidator<SystemUpdateScheduleFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SystemUpdateScheduleFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Cadence)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<UpdateCadenceKind>(s, ignoreCase: false, out _))
            .WithMessage("Cadence must be a stable UpdateCadenceKind enum-name when supplied.");
    }
}

// ─────────────────────────────── R2504 ───────────────────────────────

/// <summary>R2504 — validates <see cref="SystemUpdateEventCreateInputDto"/>.</summary>
public sealed class SystemUpdateEventCreateInputValidator : AbstractValidator<SystemUpdateEventCreateInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SystemUpdateEventCreateInputValidator()
    {
        RuleFor(x => x.ScheduleCode)
            .NotEmpty().WithMessage("ScheduleCode is required.")
            .MaximumLength(64).WithMessage("ScheduleCode must be 64 characters or fewer.");

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MinimumLength(3).WithMessage("Title must be 3 characters or more.")
            .MaximumLength(256).WithMessage("Title must be 256 characters or fewer.");

        RuleFor(x => x.Description)
            .MaximumLength(2000).WithMessage("Description must be 2000 characters or fewer.");
    }
}

/// <summary>R2504 — validates <see cref="SystemUpdateEventReasonInputDto"/>.</summary>
public sealed class SystemUpdateEventReasonInputValidator : AbstractValidator<SystemUpdateEventReasonInputDto>
{
    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SystemUpdateEventReasonInputValidator()
    {
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Reason is required.")
            .MinimumLength(3).WithMessage("Reason must be 3 characters or more.")
            .MaximumLength(500).WithMessage("Reason must be 500 characters or fewer.");
    }
}

/// <summary>R2504 — validates <see cref="SystemUpdateEventFilterDto"/>.</summary>
public sealed class SystemUpdateEventFilterValidator : AbstractValidator<SystemUpdateEventFilterDto>
{
    /// <summary>Upper bound on Take.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public SystemUpdateEventFilterValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0).WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take).InclusiveBetween(1, MaxTake).WithMessage($"Take must be in [1, {MaxTake}].");

        RuleFor(x => x.Status)
            .Must(s => string.IsNullOrEmpty(s)
                || Enum.TryParse<SystemUpdateEventStatus>(s, ignoreCase: false, out _))
            .WithMessage("Status must be a stable SystemUpdateEventStatus enum-name when supplied.");
    }
}
