using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Financials;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0816 / TOR BP 1.2-G — controller-level tests for
/// <see cref="TreasuryInformationController"/>.
/// </summary>
public sealed class TreasuryInformationControllerTests
{
    /// <summary>Builds a canonical export DTO returned by the service mock.</summary>
    private static TreasuryInformationExportDto SampleDto(string format = "XML")
        => new(
            Format: format,
            FileName: $"treasury-info-2026-05-22.{format.ToLowerInvariant()}",
            Content: System.Text.Encoding.UTF8.GetBytes("<TreasuryInformation/>"),
            RefundCount: 1,
            OutstandingClaimCount: 2,
            TotalRefundAmount: 100m,
            TotalOutstandingAmount: 500m);

    /// <summary>R0816 — GET /api/treasury/information returns 200 with application/xml.</summary>
    [Fact]
    public async Task GenerateAsync_HappyPath_ReturnsXmlFile()
    {
        var exporter = Substitute.For<ITreasuryInformationExporter>();
        exporter.GenerateAsync(Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<TreasuryInformationExportDto>.Success(SampleDto()));
        var controller = new TreasuryInformationController(exporter);

        var result = await controller.GenerateAsync(new DateOnly(2026, 5, 22), "xml", CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/xml");
        file.FileDownloadName.Should().EndWith(".xml");
    }

    /// <summary>R0816 — validation failure surfaces as a 400 ProblemDetails.</summary>
    [Fact]
    public async Task GenerateAsync_ValidationFailed_Returns400()
    {
        var exporter = Substitute.For<ITreasuryInformationExporter>();
        exporter.GenerateAsync(Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<TreasuryInformationExportDto>.Failure(
                ErrorCodes.ValidationFailed, "FOR_DATE_IN_FUTURE"));
        var controller = new TreasuryInformationController(exporter);

        var result = await controller.GenerateAsync(new DateOnly(2099, 1, 1), "xml", CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
