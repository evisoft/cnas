using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Attachments;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Services.Attachments;

/// <summary>
/// R0227 / TOR UI 014 — magic-byte sniff + filename sanitiser tests for
/// <see cref="AttachmentValidator"/>. Each case exercises one branch of the
/// validate / detect / sanitise pipeline.
/// </summary>
public sealed class AttachmentValidatorTests
{
    /// <summary>Builds the SUT with default options.</summary>
    /// <returns>The validator under test.</returns>
    private static AttachmentValidator Build(long? maxBytes = null)
    {
        var opts = new AttachmentOptions();
        if (maxBytes is not null)
        {
            opts.MaxBytes = maxBytes.Value;
        }
        return new AttachmentValidator(Options.Create(opts));
    }

    [Fact]
    public void Validate_ValidPdfMagicBytes_DetectsPdfAndSlugifies()
    {
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 }; // %PDF-1.7
        var sut = Build();

        var result = sut.Validate(bytes, "Cool Document 2026!.pdf");

        result.IsSuccess.Should().BeTrue();
        result.Value.DetectedContentType.Should().Be("application/pdf");
        result.Value.SafeFileName.Should().Be("cool-document-2026.pdf");
    }

    [Fact]
    public void Validate_EmptyBytes_RejectsValidationFailed()
    {
        var sut = Build();

        var result = sut.Validate([], "x.pdf");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Validate_ExtensionMismatch_RejectsFileTypeMismatch()
    {
        // PDF magic but declared as .docx — defence-in-depth check catches the masquerade.
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };
        var sut = Build();

        var result = sut.Validate(bytes, "trojan.docx");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.FileTypeMismatch);
    }

    [Fact]
    public void Validate_OverMaxBytes_RejectsFileTooLarge()
    {
        var bytes = new byte[1024];
        bytes[0] = 0x25; bytes[1] = 0x50; bytes[2] = 0x44; bytes[3] = 0x46; // %PDF
        var sut = Build(maxBytes: 100);

        var result = sut.Validate(bytes, "big.pdf");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.FileTooLarge);
    }

    [Fact]
    public void Validate_PathTraversalFileName_RejectsValidationFailed()
    {
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var sut = Build();

        var result = sut.Validate(bytes, "../../etc/passwd.pdf");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Validate_UnrecognisedMagicBytes_RejectsFileTypeMismatch()
    {
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        var sut = Build();

        var result = sut.Validate(bytes, "random.pdf");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.FileTypeMismatch);
    }

    [Fact]
    public void Validate_PngMagicBytes_DetectsImagePng()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var sut = Build();

        var result = sut.Validate(bytes, "photo.png");

        result.IsSuccess.Should().BeTrue();
        result.Value.DetectedContentType.Should().Be("image/png");
    }
}
