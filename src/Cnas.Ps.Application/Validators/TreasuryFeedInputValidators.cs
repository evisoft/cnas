using Cnas.Ps.Contracts;
using FluentValidation;

namespace Cnas.Ps.Application.Validators;

/// <summary>
/// R1810 / TOR BP 1.2-I — validates the imports list-filter envelope.
/// Bounds <c>Skip</c> / <c>Take</c> and ensures <c>FeedDateFrom ≤ FeedDateTo</c>
/// when both ends are supplied.
/// </summary>
public sealed class TreasuryFeedImportFilterValidator : AbstractValidator<TreasuryFeedImportFilterDto>
{
    /// <summary>Maximum permitted page size on the imports list endpoint.</summary>
    public const int MaxTake = 100;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public TreasuryFeedImportFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .WithMessage($"Take must be in [1, {MaxTake}].");

        // Bounded date range — both ends must agree when both are supplied.
        RuleFor(x => x)
            .Must(x => !x.FeedDateFrom.HasValue || !x.FeedDateTo.HasValue
                       || x.FeedDateFrom.Value <= x.FeedDateTo.Value)
            .WithMessage("FeedDateFrom must be less than or equal to FeedDateTo.");
    }
}

/// <summary>
/// R1810 / TOR BP 1.2-I — validates the rows list-filter envelope used by
/// the import-details endpoint.
/// </summary>
public sealed class TreasuryFeedImportRowFilterValidator : AbstractValidator<TreasuryFeedImportRowFilterDto>
{
    /// <summary>Maximum permitted page size on the rows-details endpoint.</summary>
    public const int MaxTake = 200;

    /// <summary>Creates the validator with every field rule wired in.</summary>
    public TreasuryFeedImportRowFilterValidator()
    {
        RuleFor(x => x.Skip)
            .GreaterThanOrEqualTo(0)
            .WithMessage("Skip must be 0 or greater.");
        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .WithMessage($"Take must be in [1, {MaxTake}].");
    }
}

/// <summary>
/// R1810 / TOR BP 1.2-I — validates the manual-import input envelope. The
/// feed date must be in the closed window <c>[today - 365 days, today]</c>;
/// future dates are refused (the Treasury cannot have produced a feed for
/// tomorrow) and operations older than 365 days are out of scope for the
/// importer's idempotent retry window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a static helper.</b> The "today" comparison depends on the
/// runtime <c>ICnasTimeProvider</c>; an instance constructor with a
/// <see cref="DateOnly"/> parameter would prevent FluentValidation's
/// auto-discovery from registering the validator. The static
/// <see cref="Validate"/> entry-point avoids that wiring while keeping the
/// per-rule messages co-located with the other Treasury feed validators.
/// </para>
/// </remarks>
public static class TreasuryFeedManualImportInputValidator
{
    /// <summary>Maximum number of days in the past that the manual entry will accept.</summary>
    public const int MaxPastDays = 365;

    /// <summary>Stable validation failure when the supplied date is in the future.</summary>
    public const string FutureDateMessage = "FeedDate cannot be in the future.";

    /// <summary>Stable validation failure when the supplied date is older than the retry window.</summary>
    public static readonly string TooOldDateMessage
        = $"FeedDate cannot be more than {MaxPastDays} days in the past.";

    /// <summary>
    /// Returns <c>null</c> when <paramref name="feedDate"/> is in the closed
    /// window <c>[today - MaxPastDays, today]</c>; otherwise returns the
    /// stable rule-violation message.
    /// </summary>
    /// <param name="feedDate">Candidate feed date.</param>
    /// <param name="today">UTC today supplied by <c>ICnasTimeProvider</c>.</param>
    /// <returns>Validation message, or <c>null</c> on success.</returns>
    public static string? Validate(DateOnly feedDate, DateOnly today)
    {
        if (feedDate > today)
        {
            return FutureDateMessage;
        }
        if (feedDate < today.AddDays(-MaxPastDays))
        {
            return TooOldDateMessage;
        }
        return null;
    }
}
