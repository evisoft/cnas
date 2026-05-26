using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R1810 / TOR BP 1.2-I — unit tests for the Treasury feed input
/// validators. Exercises the imports filter envelope, the rows filter
/// envelope, and the manual-import static-helper boundary rules.
/// </summary>
public sealed class TreasuryFeedInputValidatorTests
{
    /// <summary>Canonical "today" used across manual-import tests.</summary>
    private static readonly DateOnly Today = new(2026, 5, 23);

    /// <summary>
    /// A filter envelope with Skip=0, Take=50, and matching from/to dates
    /// passes the validator unchanged.
    /// </summary>
    [Fact]
    public void Filter_HappyPath_Passes()
    {
        var v = new TreasuryFeedImportFilterValidator();
        var filter = new TreasuryFeedImportFilterDto(
            Status: "Completed",
            FeedDateFrom: new DateOnly(2026, 5, 1),
            FeedDateTo: new DateOnly(2026, 5, 23),
            TriggerKind: "Scheduled",
            Skip: 0,
            Take: 50);

        var result = v.Validate(filter);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>
    /// FeedDateFrom &gt; FeedDateTo is refused with the canonical message.
    /// </summary>
    [Fact]
    public void Filter_FromAfterTo_IsRejected()
    {
        var v = new TreasuryFeedImportFilterValidator();
        var filter = new TreasuryFeedImportFilterDto(
            FeedDateFrom: new DateOnly(2026, 5, 23),
            FeedDateTo: new DateOnly(2026, 5, 1));

        var result = v.Validate(filter);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("FeedDateFrom", StringComparison.Ordinal));
    }

    /// <summary>
    /// A manual-import feed date in the future is rejected.
    /// </summary>
    [Fact]
    public void Manual_FutureDate_IsRejected()
    {
        var violation = TreasuryFeedManualImportInputValidator.Validate(Today.AddDays(1), Today);

        violation.Should().Be(TreasuryFeedManualImportInputValidator.FutureDateMessage);
    }

    /// <summary>
    /// A manual-import feed date older than the retry window is rejected.
    /// </summary>
    [Fact]
    public void Manual_TooOldDate_IsRejected()
    {
        var violation = TreasuryFeedManualImportInputValidator.Validate(
            Today.AddDays(-TreasuryFeedManualImportInputValidator.MaxPastDays - 1),
            Today);

        violation.Should().Be(TreasuryFeedManualImportInputValidator.TooOldDateMessage);
    }

    /// <summary>
    /// Manual-import feed date equal to today is accepted.
    /// </summary>
    [Fact]
    public void Manual_Today_IsAccepted()
    {
        var violation = TreasuryFeedManualImportInputValidator.Validate(Today, Today);

        violation.Should().BeNull();
    }

    /// <summary>
    /// Rows filter Take above 200 is rejected.
    /// </summary>
    [Fact]
    public void RowsFilter_TakeAboveMax_IsRejected()
    {
        var v = new TreasuryFeedImportRowFilterValidator();
        var filter = new TreasuryFeedImportRowFilterDto(Skip: 0, Take: 201);

        var result = v.Validate(filter);

        result.IsValid.Should().BeFalse();
    }
}
