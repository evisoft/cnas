using System.Text.RegularExpressions;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Domain;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0128 / R0173 — validator for <see cref="WorkflowNotificationStrategyUpsertInput"/>.
/// Enforces the recipient-role allow-list, the channel non-emptiness invariant for
/// enabled strategies, and the quiet-hours pairing rule. The (workflow, event code)
/// natural key lives in the route — validating it is the service layer's job.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recipient role grammar.</b> Roles are restricted to the frozen set
/// <c>Assignee | AssigneeSupervisor | Applicant | ProcessOwner | ApprovingManager</c>
/// plus parametrised custom groups matching <c>CustomGroup:&lt;code&gt;</c>. Group
/// codes inside the custom-group form must match
/// <c>[a-zA-Z0-9._-]{1,64}</c> — same character class used by user-administration
/// group codes elsewhere in the system.
/// </para>
/// <para>
/// <b>Channels.</b> Strings parsed against the <see cref="NotificationChannel"/> enum;
/// unknown strings fail validation. Empty lists are rejected ONLY when the strategy is
/// enabled — a disabled strategy with no channels is a legitimate "do not notify"
/// instruction.
/// </para>
/// <para>
/// <b>Quiet hours.</b> Both Start and End must be supplied together (both null OR both
/// non-null in 0..1439). Wrapping windows (Start &gt; End) are legal — quiet hours
/// crossing midnight (e.g. 22:00..06:00) are a common operator request.
/// </para>
/// </remarks>
public sealed class WorkflowNotificationStrategyUpsertInputValidator
    : AbstractValidator<WorkflowNotificationStrategyUpsertInput>
{
    /// <summary>
    /// Allow-list regex for <see cref="WorkflowNotificationStrategyUpsertInput.RecipientRoles"/>
    /// entries — matches one of the five canonical role codes OR the parametrised
    /// custom-group form. Anchored so a substring like <c>"AssigneeSupervisorXYZ"</c>
    /// is rejected.
    /// </summary>
    internal const string RecipientRolePattern =
        "^(Assignee|AssigneeSupervisor|Applicant|ProcessOwner|ApprovingManager|CustomGroup:[a-zA-Z0-9._-]{1,64})$";

    private static readonly Regex RecipientRoleRegex = new(
        RecipientRolePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>Creates the validator with the full rule set.</summary>
    public WorkflowNotificationStrategyUpsertInputValidator()
    {
        RuleFor(x => x.Channels)
            .NotNull().WithMessage("Channels list is required (may be empty when IsEnabled=false).");

        RuleFor(x => x.RecipientRoles)
            .NotNull().WithMessage("RecipientRoles list is required.");

        // When the strategy is enabled, at least one channel must be configured.
        // A disabled strategy may legitimately list zero channels — it's the explicit
        // "do not notify" override and channels are irrelevant.
        RuleFor(x => x)
            .Must(input => !input.IsEnabled || (input.Channels is not null && input.Channels.Count > 0))
            .WithName(nameof(WorkflowNotificationStrategyUpsertInput.Channels))
            .WithMessage("Channels must contain at least one entry when IsEnabled=true.");

        // Channel string allow-list — must parse as NotificationChannel.
        RuleForEach(x => x.Channels)
            .Must(ChannelStringIsValid)
            .WithMessage("Channel must be one of: Email, Sms, InApp.");

        // RecipientRoles allow-list.
        RuleForEach(x => x.RecipientRoles)
            .Must(RecipientRoleIsValid)
            .WithMessage(
                "RecipientRole must match "
                + "^(Assignee|AssigneeSupervisor|Applicant|ProcessOwner|ApprovingManager|CustomGroup:[a-zA-Z0-9._-]{1,64})$");

        // TemplateCodeOverride: optional, capped.
        RuleFor(x => x.TemplateCodeOverride)
            .MaximumLength(64).WithMessage("TemplateCodeOverride exceeds the 64-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.TemplateCodeOverride));

        // Quiet hours pairing + range.
        RuleFor(x => x)
            .Must(QuietHoursPairingIsValid)
            .WithName(nameof(WorkflowNotificationStrategyUpsertInput.QuietHoursStart))
            .WithMessage(
                "QuietHoursStart and QuietHoursEnd must be both set or both null; values 0..1439.");

        RuleFor(x => x.Description)
            .MaximumLength(512).WithMessage("Description exceeds the 512-character cap.")
            .When(x => !string.IsNullOrWhiteSpace(x.Description));
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> is parseable as a defined
    /// <see cref="NotificationChannel"/> enum value. Case-sensitive — operators are
    /// expected to use the canonical names (<c>Email</c>, <c>Sms</c>, <c>InApp</c>).
    /// </summary>
    /// <param name="value">Caller-supplied channel string.</param>
    /// <returns><c>true</c> when the string parses to a defined enum value.</returns>
    internal static bool ChannelStringIsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        return Enum.TryParse<NotificationChannel>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> matches the recipient-role
    /// allow-list. Anchored regex match — substring matches are rejected.
    /// </summary>
    /// <param name="value">Caller-supplied recipient role code.</param>
    /// <returns><c>true</c> when the role is on the allow-list.</returns>
    internal static bool RecipientRoleIsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        try
        {
            return RecipientRoleRegex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the quiet-hours pair is internally consistent:
    /// both null (no window), or both in 0..1439.
    /// </summary>
    /// <param name="input">Upsert payload under validation.</param>
    /// <returns><c>true</c> when the pair is valid.</returns>
    internal static bool QuietHoursPairingIsValid(WorkflowNotificationStrategyUpsertInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Both null: no quiet hours.
        if (input.QuietHoursStart is null && input.QuietHoursEnd is null)
        {
            return true;
        }
        // One null, the other not: invalid.
        if (input.QuietHoursStart is null || input.QuietHoursEnd is null)
        {
            return false;
        }
        // Both set: range check.
        return input.QuietHoursStart is >= 0 and <= 1439
            && input.QuietHoursEnd is >= 0 and <= 1439;
    }

    /// <summary>
    /// Validates that <paramref name="eventCode"/> is one of the canonical
    /// <see cref="WorkflowNotificationEvents.All"/> entries. Exposed as a static
    /// helper so the CRUD service can reject an unknown route segment before invoking
    /// the body validator.
    /// </summary>
    /// <param name="eventCode">Caller-supplied event code from the route.</param>
    /// <returns><c>true</c> when the code is on the allow-list.</returns>
    public static bool EventCodeIsKnown(string? eventCode)
    {
        if (string.IsNullOrWhiteSpace(eventCode))
        {
            return false;
        }
        return WorkflowNotificationEvents.All.Contains(eventCode);
    }
}
