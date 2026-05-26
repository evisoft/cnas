using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0514 / TOR CF 02.02 — service-level tests for
/// <see cref="PensionCalculatorService"/>. Exercises the linear projection
/// formula, the gender-default retirement age, the
/// <c>Pension.SimulateAdvanced</c> permission gate around the coefficient
/// override, the input validator, and the audit-row contract.
/// </summary>
public sealed class PensionCalculatorServiceTests
{
    /// <summary>Builds a configured options instance backing the default formula constants.</summary>
    private static IOptions<PensionOptions> DefaultOptions() => Options.Create(new PensionOptions());

    /// <summary>Builds an authenticated caller with no extra permissions.</summary>
    private static ICallerContext NewStandardCaller(params string[] roles)
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(42L);
        caller.UserSqid.Returns("USR-42");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-pension");
        caller.Roles.Returns(roles);
        return caller;
    }

    /// <summary>Captures audit invocations so tests can assert payload shape.</summary>
    private static (IAuditService Audit, Func<(string Code, AuditSeverity Severity, string? Details)?> Last) NewAuditCapture()
    {
        (string Code, AuditSeverity Severity, string? Details)? slot = null;
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Do<string>(s => slot = ((slot ?? default).Code is null
                    ? (s, default, null)
                    : (s, slot!.Value.Severity, slot!.Value.Details))),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                slot = (call.ArgAt<string>(0), call.ArgAt<AuditSeverity>(1), call.ArgAt<string>(5));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => slot);
    }

    /// <summary>Builds the SUT around the supplied collaborators.</summary>
    private static PensionCalculatorService NewService(
        ICallerContext caller,
        IAuditService audit,
        PensionOptions? options = null)
    {
        var validator = new PensionSimulationInputValidator();
        var opts = options is null ? DefaultOptions() : Options.Create(options);
        return new PensionCalculatorService(validator, opts, caller, audit);
    }

    /// <summary>
    /// R0514 / Test 1 — small projection result hits the floor and the floor
    /// is substituted, FloorApplied flips to true.
    /// </summary>
    [Fact]
    public async Task R0514_Simulate_BelowFloor_AppliesFloor_AndFlipsFlag()
    {
        var (audit, _) = NewAuditCapture();
        var sut = NewService(NewStandardCaller(), audit);

        var input = new PensionSimulationInputDto(
            YearsOfService: 25,
            AverageMonthlyContributionBase: 5000m,
            CurrentAge: 50,
            RetirementAge: 63,
            Gender: "M",
            CoefficientOverride: null);

        var result = await sut.SimulateAsync(input);

        result.IsSuccess.Should().BeTrue();
        // Raw formula: 5000 * 0.0135 * 25 = 1687.50 — below floor 2000.00.
        result.Value.EstimatedMonthlyPension.Should().Be(2000.00m);
        result.Value.FloorApplied.Should().BeTrue();
        result.Value.MinPensionFloor.Should().Be(2000.00m);
        result.Value.AccrualCoefficient.Should().Be(1.35m);
        result.Value.YearsUntilRetirement.Should().Be(13);
    }

    /// <summary>
    /// R0514 / Test 2 — projection above the floor is returned verbatim,
    /// FloorApplied stays false.
    /// </summary>
    [Fact]
    public async Task R0514_Simulate_AboveFloor_ReturnsComputedAmount_NoFloorApplied()
    {
        var (audit, _) = NewAuditCapture();
        var sut = NewService(NewStandardCaller(), audit);

        var input = new PensionSimulationInputDto(
            YearsOfService: 40,
            AverageMonthlyContributionBase: 8000m,
            CurrentAge: 60,
            RetirementAge: 63,
            Gender: "M",
            CoefficientOverride: null);

        var result = await sut.SimulateAsync(input);

        result.IsSuccess.Should().BeTrue();
        // 8000 * 0.0135 * 40 = 4320.00 — above floor 2000.00.
        result.Value.EstimatedMonthlyPension.Should().Be(4320.00m);
        result.Value.FloorApplied.Should().BeFalse();
    }

    /// <summary>
    /// R0514 / Test 3 — female gender default substitutes 60 when
    /// RetirementAge is omitted.
    /// </summary>
    [Fact]
    public async Task R0514_Simulate_FemaleDefault_Substitutes60ForRetirementAge()
    {
        var (audit, _) = NewAuditCapture();
        var sut = NewService(NewStandardCaller(), audit);

        var input = new PensionSimulationInputDto(
            YearsOfService: 30,
            AverageMonthlyContributionBase: 10000m,
            CurrentAge: 55,
            RetirementAge: null,
            Gender: "F",
            CoefficientOverride: null);

        var result = await sut.SimulateAsync(input);

        result.IsSuccess.Should().BeTrue();
        // YearsUntilRetirement = 60 - 55 = 5.
        result.Value.YearsUntilRetirement.Should().Be(5);
    }

    /// <summary>
    /// R0514 / Test 4 — caller without <c>Pension.SimulateAdvanced</c> sees
    /// the coefficient override ignored.
    /// </summary>
    [Fact]
    public async Task R0514_Simulate_CoefficientOverride_IgnoredWithoutAdvancedPermission()
    {
        var (audit, _) = NewAuditCapture();
        var sut = NewService(NewStandardCaller(/* no advanced */), audit);

        var input = new PensionSimulationInputDto(
            YearsOfService: 40,
            AverageMonthlyContributionBase: 8000m,
            CurrentAge: 60,
            RetirementAge: 63,
            Gender: "M",
            CoefficientOverride: 5.00m); // would inflate massively if honoured

        var result = await sut.SimulateAsync(input);

        result.IsSuccess.Should().BeTrue();
        // Should still use 1.35 default — 8000 * 0.0135 * 40 = 4320.00.
        result.Value.AccrualCoefficient.Should().Be(1.35m);
        result.Value.EstimatedMonthlyPension.Should().Be(4320.00m);
    }

    /// <summary>
    /// R0514 / Test 5 — caller WITH <c>Pension.SimulateAdvanced</c> has the
    /// coefficient override honoured.
    /// </summary>
    [Fact]
    public async Task R0514_Simulate_CoefficientOverride_HonouredWithAdvancedPermission()
    {
        var (audit, _) = NewAuditCapture();
        var sut = NewService(
            NewStandardCaller(PensionCalculatorService.AdvancedPermission),
            audit);

        var input = new PensionSimulationInputDto(
            YearsOfService: 40,
            AverageMonthlyContributionBase: 8000m,
            CurrentAge: 60,
            RetirementAge: 63,
            Gender: "M",
            CoefficientOverride: 2.00m);

        var result = await sut.SimulateAsync(input);

        result.IsSuccess.Should().BeTrue();
        // 8000 * 0.02 * 40 = 6400.00.
        result.Value.AccrualCoefficient.Should().Be(2.00m);
        result.Value.EstimatedMonthlyPension.Should().Be(6400.00m);
    }

    /// <summary>
    /// R0514 / Test 6 — validator rejects YearsOfService = 80.
    /// </summary>
    [Fact]
    public async Task R0514_Simulate_OutOfRangeYearsOfService_ReturnsValidationFailed()
    {
        var (audit, _) = NewAuditCapture();
        var sut = NewService(NewStandardCaller(), audit);

        var input = new PensionSimulationInputDto(
            YearsOfService: 80,
            AverageMonthlyContributionBase: 8000m,
            CurrentAge: 60,
            RetirementAge: 63,
            Gender: "M",
            CoefficientOverride: null);

        var result = await sut.SimulateAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// R0514 / Test 7 — audit Information row is written with the formula
    /// inputs and the projected amount.
    /// </summary>
    [Fact]
    public async Task R0514_Simulate_AuditRow_CarriesFormulaInputsAndResult()
    {
        var (audit, lastAudit) = NewAuditCapture();
        var sut = NewService(NewStandardCaller(), audit);

        var input = new PensionSimulationInputDto(
            YearsOfService: 40,
            AverageMonthlyContributionBase: 8000m,
            CurrentAge: 60,
            RetirementAge: 63,
            Gender: "M",
            CoefficientOverride: null);

        var result = await sut.SimulateAsync(input);

        result.IsSuccess.Should().BeTrue();
        var captured = lastAudit();
        captured.Should().NotBeNull();
        captured!.Value.Code.Should().Be(PensionCalculatorService.AuditEventCode);
        captured.Value.Severity.Should().Be(AuditSeverity.Information);
        captured.Value.Details.Should().NotBeNull();
        using var doc = JsonDocument.Parse(captured.Value.Details!);
        doc.RootElement.GetProperty("yearsOfService").GetInt32().Should().Be(40);
        doc.RootElement.GetProperty("retirementAge").GetInt32().Should().Be(63);
        doc.RootElement.GetProperty("result").GetDecimal().Should().Be(4320.00m);
    }
}
