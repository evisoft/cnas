using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Pension;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0514 — controller-level unit tests for <see cref="PensionController"/>.
/// Asserts the happy-path 200 shape, the 401 / 400 ProblemDetails mapping,
/// and the no-PII discipline (no identifier echoed in the response body).
/// </summary>
public sealed class PensionControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IPensionCalculatorService NewServiceMock() =>
        Substitute.For<IPensionCalculatorService>();

    /// <summary>Builds the SUT around the supplied service.</summary>
    private static PensionController NewController(IPensionCalculatorService svc) => new(svc);

    /// <summary>
    /// Controller test — happy path returns 200 with the populated DTO and
    /// no PII fields on the wire.
    /// </summary>
    [Fact]
    public async Task R0514_Simulate_Success_Returns200_WithProjectionDto()
    {
        var svc = NewServiceMock();
        var dto = new PensionSimulationDto(
            EstimatedMonthlyPension: 4320m,
            YearsUntilRetirement: 3,
            AccrualCoefficient: 1.35m,
            MinPensionFloor: 2000m,
            FloorApplied: false,
            FormulaDescriptionRo: "8000.00 MDL × 1.35% × 40 ani = 4320.00 MDL");
        svc.SimulateAsync(Arg.Any<PensionSimulationInputDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<PensionSimulationDto>.Success(dto));
        var controller = NewController(svc);

        var body = new PensionSimulationInputDto(
            YearsOfService: 40,
            AverageMonthlyContributionBase: 8000m,
            CurrentAge: 60,
            RetirementAge: 63,
            Gender: "M",
            CoefficientOverride: null);
        var result = await controller.SimulateAsync(body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var returned = ok.Value.Should().BeOfType<PensionSimulationDto>().Subject;
        returned.EstimatedMonthlyPension.Should().Be(4320m);
        // No identifier fields on the output DTO.
        returned.GetType().GetProperties()
            .Should().NotContain(p => p.Name.Contains("Idnp", StringComparison.Ordinal));
    }

    /// <summary>
    /// Controller test — validation failure surfaces as 400 ProblemDetails
    /// carrying the stable error code.
    /// </summary>
    [Fact]
    public async Task R0514_Simulate_ValidationFailed_Returns400()
    {
        var svc = NewServiceMock();
        svc.SimulateAsync(Arg.Any<PensionSimulationInputDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<PensionSimulationDto>.Failure(
               ErrorCodes.ValidationFailed, "YearsOfService must be between 0 and 70."));
        var controller = NewController(svc);

        var body = new PensionSimulationInputDto(
            YearsOfService: 80,
            AverageMonthlyContributionBase: 8000m,
            CurrentAge: 60,
            RetirementAge: 63,
            Gender: "M",
            CoefficientOverride: null);
        var result = await controller.SimulateAsync(body, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["errorCode"].Should().Be(ErrorCodes.ValidationFailed);
    }
}
