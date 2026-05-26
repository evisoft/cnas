using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0227 / TOR UI 014 — input-validation rules for <see cref="AttachmentUploadDtoValidator"/>.
/// Each test exercises one branch of the rule set: owner-type allow-list, base64
/// well-formedness + size cap, filename shape (extension required, no path separators),
/// category + sensitivity enum parsing, description length.
/// </summary>
public sealed class AttachmentUploadDtoValidatorTests
{
    /// <summary>Builds a canonical valid DTO that callers tweak in each test.</summary>
    /// <returns>A baseline DTO that passes validation.</returns>
    private static AttachmentUploadDto Valid() => new(
        OwnerEntityType: "ServiceApplication",
        OwnerSqid: "SQID-42",
        ContentBase64: Convert.ToBase64String([0x25, 0x50, 0x44, 0x46, 0x2D]),
        DeclaredFileName: "income-proof.pdf",
        Category: "Income",
        SensitivityLabel: "Confidential",
        Description: "Bank statement for Q1 2026");

    [Fact]
    public void Validate_Baseline_Succeeds()
    {
        var sut = new AttachmentUploadDtoValidator();

        var result = sut.Validate(Valid());

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsUnknownOwnerType()
    {
        var sut = new AttachmentUploadDtoValidator();
        var dto = Valid() with { OwnerEntityType = "Lizard" };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AttachmentUploadDto.OwnerEntityType));
    }

    [Fact]
    public void Validate_RejectsPathTraversalFileName()
    {
        var sut = new AttachmentUploadDtoValidator();
        var dto = Valid() with { DeclaredFileName = "../../etc/passwd" };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AttachmentUploadDto.DeclaredFileName));
    }

    [Fact]
    public void Validate_RejectsFileNameWithoutExtension()
    {
        var sut = new AttachmentUploadDtoValidator();
        var dto = Valid() with { DeclaredFileName = "noextension" };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AttachmentUploadDto.DeclaredFileName));
    }

    [Fact]
    public void Validate_RejectsInvalidBase64()
    {
        var sut = new AttachmentUploadDtoValidator();
        var dto = Valid() with { ContentBase64 = "!!! not base64 !!!" };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AttachmentUploadDto.ContentBase64));
    }

    [Fact]
    public void Validate_RejectsUnknownCategory()
    {
        var sut = new AttachmentUploadDtoValidator();
        var dto = Valid() with { Category = "NotACategory" };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AttachmentUploadDto.Category));
    }

    [Fact]
    public void Validate_RejectsUnknownSensitivityLabel()
    {
        var sut = new AttachmentUploadDtoValidator();
        var dto = Valid() with { SensitivityLabel = "Top-Secret" };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AttachmentUploadDto.SensitivityLabel));
    }

    [Fact]
    public void Validate_AllowsNullSensitivityLabel()
    {
        var sut = new AttachmentUploadDtoValidator();
        var dto = Valid() with { SensitivityLabel = null };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }

    [Fact]
    public void Validate_RejectsOversizedDescription()
    {
        var sut = new AttachmentUploadDtoValidator();
        var dto = Valid() with { Description = new string('x', 600) };

        var result = sut.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AttachmentUploadDto.Description));
    }
}
