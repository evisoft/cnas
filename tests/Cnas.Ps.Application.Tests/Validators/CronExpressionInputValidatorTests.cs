using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — unit tests for
/// <see cref="CronExpressionInputValidator"/>. Pins the parse contract, the
/// length cap, the non-empty rule, and the once-per-minute floor.
/// </summary>
public sealed class CronExpressionInputValidatorTests
{
    /// <summary>A standard Quartz cron expression (every minute on the second boundary) passes.</summary>
    [Fact]
    public void Validate_ValidCron_Passes()
    {
        var v = new CronExpressionInputValidator();
        var input = new CronExpressionInputDto("0 0/1 * * * ?");

        var result = v.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>A clearly malformed expression is rejected with a parse-failure message.</summary>
    [Fact]
    public void Validate_InvalidSyntax_Rejected()
    {
        var v = new CronExpressionInputValidator();
        var input = new CronExpressionInputDto("not a cron at all");

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CronExpressionInputDto.CronExpression));
    }

    /// <summary>An empty expression is rejected with the required-field message.</summary>
    [Fact]
    public void Validate_Empty_Rejected()
    {
        var v = new CronExpressionInputValidator();
        var input = new CronExpressionInputDto(string.Empty);

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CronExpressionInputDto.CronExpression));
    }

    /// <summary>
    /// A wildcard-seconds expression (fires every second) is rejected by the
    /// too-frequent rule even though it parses through Quartz.
    /// </summary>
    [Fact]
    public void Validate_FiresEverySecond_Rejected()
    {
        var v = new CronExpressionInputValidator();
        var input = new CronExpressionInputDto("* * * * * ?");

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(CronExpressionInputDto.CronExpression));
    }

    /// <summary>
    /// A step-seconds expression (every 30 seconds) parses through Quartz AND
    /// keeps the seconds field bounded, so the validator accepts it.
    /// </summary>
    [Fact]
    public void Validate_StepSecondsCron_Passes()
    {
        var v = new CronExpressionInputValidator();
        var input = new CronExpressionInputDto("0/30 * * * * ?");

        var result = v.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>An expression past the 200-char cap is rejected.</summary>
    [Fact]
    public void Validate_OverLongCron_Rejected()
    {
        var v = new CronExpressionInputValidator();
        var input = new CronExpressionInputDto(new string('a', CronExpressionInputValidator.MaxLength + 1));

        var result = v.Validate(input);

        result.IsValid.Should().BeFalse();
    }
}
