using Cnas.Ps.Contracts;
using FluentValidation;
using Quartz;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — validates <see cref="CronExpressionInputDto"/>.
/// Pins a non-empty, ≤ 200-char cron expression that parses through
/// <see cref="CronExpression.IsValidExpression"/> AND that fires at most once per
/// minute (cron expressions that would fire many times per second create a runaway
/// scheduler — we reject anything that fires more often than the Quartz minimum
/// safe cadence).
/// </summary>
/// <remarks>
/// <para>
/// <b>Too-frequent rule.</b> Quartz allows wildcard seconds (<c>* * * * * ?</c>) which
/// would fire every second. For SI PS the operationally safe minimum is once per minute
/// so we reject any expression whose seconds-field is the wildcard <c>*</c> without an
/// explicit step. Operators who genuinely need sub-minute cadence file a configuration
/// change ticket and we adjust the validator alongside it.
/// </para>
/// </remarks>
public sealed class CronExpressionInputValidator
    : AbstractValidator<CronExpressionInputDto>
{
    /// <summary>Maximum cron-expression length accepted by the validator.</summary>
    public const int MaxLength = 200;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public CronExpressionInputValidator()
    {
        RuleFor(x => x.CronExpression)
            .NotEmpty().WithMessage("CronExpression is required.")
            .MaximumLength(MaxLength)
            .WithMessage($"CronExpression must be {MaxLength} characters or fewer.")
            .Must(IsParsableCron)
            .WithMessage("CronExpression is not valid Quartz cron syntax.")
            .Must(IsNotTooFrequent)
            .WithMessage("CronExpression fires more often than once per minute, which is not permitted.");
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="expression"/> parses as a valid Quartz
    /// cron expression.
    /// </summary>
    /// <param name="expression">Candidate cron expression.</param>
    /// <returns><c>true</c> iff Quartz accepts the expression.</returns>
    internal static bool IsParsableCron(string? expression)
        => !string.IsNullOrWhiteSpace(expression)
           && CronExpression.IsValidExpression(expression);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="expression"/> does NOT fire more often
    /// than once per minute. Concretely: the seconds field (first token) cannot be the
    /// bare wildcard <c>*</c>.
    /// </summary>
    /// <param name="expression">Candidate cron expression (assumed already non-null /
    /// non-empty by an earlier rule).</param>
    /// <returns><c>true</c> iff the cadence is at least one minute.</returns>
    internal static bool IsNotTooFrequent(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return true; // earlier rule handles null
        var trimmed = expression.Trim();
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace <= 0) return true; // earlier IsParsableCron rule already failed
        var secondsField = trimmed[..firstSpace];
        // Bare wildcard means "every second" — reject. Step expressions (e.g. */15) are
        // allowed because they limit the cadence to once every N seconds.
        return secondsField != "*";
    }
}
