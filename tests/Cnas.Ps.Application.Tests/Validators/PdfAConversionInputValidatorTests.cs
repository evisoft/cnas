using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0341 / TOR CF 11.06 — unit tests for the PDF/A conversion input
/// validator. Pins the byte-length bounds and the conformance enum check.
/// </summary>
public sealed class PdfAConversionInputValidatorTests
{
    /// <summary>Happy-path conversion input (non-empty, default conformance) passes.</summary>
    [Fact]
    public void Validator_AcceptsCanonicalInput()
    {
        var validator = new PdfAConversionInputValidator();
        var input = new PdfAConversionInputDto(
            SourcePdfBytes: new byte[100],
            TargetConformance: PdfAConformanceLevel.Pdf_A_2u);

        var result = validator.Validate(input);

        result.IsValid.Should().BeTrue();
    }

    /// <summary>Empty bytes array is rejected (below MinSourceBytes).</summary>
    [Fact]
    public void Validator_RejectsEmptyByteArray()
    {
        var validator = new PdfAConversionInputValidator();
        var input = new PdfAConversionInputDto(SourcePdfBytes: Array.Empty<byte>());

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(PdfAConversionInputDto.SourcePdfBytes));
    }

    /// <summary>Above-cap bytes array is rejected (> MaxSourceBytes).</summary>
    [Fact]
    public void Validator_RejectsAboveCapByteArray()
    {
        var validator = new PdfAConversionInputValidator();
        var input = new PdfAConversionInputDto(
            SourcePdfBytes: new byte[PdfAConversionInputValidator.MaxSourceBytes + 1]);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>Out-of-enum conformance level is rejected.</summary>
    [Fact]
    public void Validator_RejectsUnknownConformance()
    {
        var validator = new PdfAConversionInputValidator();
        var input = new PdfAConversionInputDto(
            SourcePdfBytes: new byte[100],
            TargetConformance: (PdfAConformanceLevel)999);

        var result = validator.Validate(input);

        result.IsValid.Should().BeFalse();
    }
}
