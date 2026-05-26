using Cnas.Ps.Application.Documents;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Infrastructure.Services.Documents;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Documents;

/// <summary>
/// R0341 / TOR CF 11.06 — tests for <see cref="PdfAConversionService"/>.
/// The placeholder returns <c>PDFA.ENGINE_NOT_AVAILABLE</c> deterministically
/// until a license-cleared engine is wired in.
/// </summary>
public sealed class PdfAConversionServiceTests
{
    /// <summary>Blank Engine returns the deterministic ENGINE_NOT_AVAILABLE failure.</summary>
    [Fact]
    public async Task ConvertAsync_BlankEngine_ReturnsEngineNotAvailable()
    {
        var svc = new PdfAConversionService(
            Options.Create(new PdfAConversionOptions { Engine = string.Empty }),
            new PdfAConversionInputValidator());

        var result = await svc.ConvertAsync(new PdfAConversionInputDto(
            SourcePdfBytes: new byte[100],
            TargetConformance: PdfAConformanceLevel.Pdf_A_2u));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(IPdfAConversionService.EngineNotAvailableCode);
    }

    /// <summary>
    /// Even a recognised engine name still returns the deterministic failure
    /// in this iteration — the real conversion library is out of scope.
    /// </summary>
    [Fact]
    public async Task ConvertAsync_NamedButPlaceholderEngine_StillReturnsEngineNotAvailable()
    {
        var svc = new PdfAConversionService(
            Options.Create(new PdfAConversionOptions { Engine = "PdfPig" }),
            new PdfAConversionInputValidator());

        var result = await svc.ConvertAsync(new PdfAConversionInputDto(
            SourcePdfBytes: new byte[100]));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(IPdfAConversionService.EngineNotAvailableCode);
        result.ErrorMessage.Should().Contain("PdfPig");
    }
}
