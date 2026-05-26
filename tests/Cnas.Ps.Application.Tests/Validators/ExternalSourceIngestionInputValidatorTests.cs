using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0203 / TOR CF 20.06 — unit tests for the manual-trigger + filter
/// validators. Pins the SourceCode regex, paging caps, enum-name membership,
/// and the as-of-date helper.
/// </summary>
public sealed class ExternalSourceIngestionInputValidatorTests
{
    /// <summary>Happy-path SourceCode passes.</summary>
    [Fact]
    public void Validator_AcceptsCanonicalRspSourceCode()
    {
        var validator = new ExternalSourceManualTriggerInputValidator();
        var input = new ExternalSourceManualTriggerInputDto("RSP");

        var result = validator.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>Lower-case SourceCode is rejected by the regex (enforces SCREAMING_SNAKE_CASE).</summary>
    [Fact]
    public void Validator_RejectsLowerCaseSourceCode()
    {
        var validator = new ExternalSourceManualTriggerInputValidator();
        var input = new ExternalSourceManualTriggerInputDto("rsp");

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(ExternalSourceManualTriggerInputDto.SourceCode));
    }

    /// <summary>Empty SourceCode is rejected.</summary>
    [Fact]
    public void Validator_RejectsEmptySourceCode()
    {
        var validator = new ExternalSourceManualTriggerInputValidator();
        var input = new ExternalSourceManualTriggerInputDto(string.Empty);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>AsOfDate in the future is flagged by the date helper.</summary>
    [Fact]
    public void AsOfDate_FutureIsRejected()
    {
        var todayUtc = new DateOnly(2026, 5, 24);
        var future = todayUtc.AddDays(1);

        var violation = ExternalSourceManualTriggerInputValidator.ValidateAsOfDate(future, todayUtc);

        violation.Should().NotBeNull();
        violation.Should().Contain("future");
    }

    /// <summary>AsOfDate older than 365 days is flagged.</summary>
    [Fact]
    public void AsOfDate_TooOldIsRejected()
    {
        var todayUtc = new DateOnly(2026, 5, 24);
        var ancient = todayUtc.AddDays(-400);

        var violation = ExternalSourceManualTriggerInputValidator.ValidateAsOfDate(ancient, todayUtc);

        violation.Should().NotBeNull();
        violation.Should().Contain("365");
    }

    /// <summary>Filter Take above cap rejected.</summary>
    [Fact]
    public void FilterValidator_RejectsTakeAboveCap()
    {
        var validator = new ExternalSourceIngestionRunFilterValidator();
        var input = new ExternalSourceIngestionRunFilterDto(Take: 500);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(ExternalSourceIngestionRunFilterDto.Take));
    }

    /// <summary>Filter unknown Status enum-name rejected.</summary>
    [Fact]
    public void FilterValidator_RejectsUnknownStatus()
    {
        var validator = new ExternalSourceIngestionRunFilterValidator();
        var input = new ExternalSourceIngestionRunFilterDto(Status: "Bogus");

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
    }
}
