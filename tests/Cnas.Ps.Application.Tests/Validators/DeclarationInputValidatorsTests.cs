using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0810 / R0811 / R0812 / R0813 — unit tests for the declaration input-DTO
/// validators. Each contract clause is locked down with one positive and one
/// negative case so the wire shape stays stable across releases.
/// </summary>
public sealed class DeclarationInputValidatorsTests
{
    /// <summary>Canonical first-of-month anchor used across the suite.</summary>
    private static readonly DateOnly FirstOfMonth = new(2026, 5, 1);

    /// <summary>BP 1.2-A — happy path: full SFS input passes.</summary>
    [Fact]
    public void Sfs_HappyPath_Passes()
    {
        var v = new DeclarationFromSfsInputDtoValidator();
        var result = v.Validate(new DeclarationFromSfsInputDto(
            ContributorSqid: "SQID-1",
            ReportingMonth: FirstOfMonth,
            ReferenceNumber: "SFS-001",
            DeclaredContributionAmount: 1234.56m));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>BP 1.2-A — non-first-of-month is rejected.</summary>
    [Fact]
    public void Sfs_BadDay_Fails()
    {
        var v = new DeclarationFromSfsInputDtoValidator();
        var result = v.Validate(new DeclarationFromSfsInputDto(
            ContributorSqid: "SQID-1",
            ReportingMonth: new DateOnly(2026, 5, 15),
            ReferenceNumber: "SFS-001",
            DeclaredContributionAmount: 1m));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>BP 1.2-A — negative amount is rejected.</summary>
    [Fact]
    public void Sfs_NegativeAmount_Fails()
    {
        var v = new DeclarationFromSfsInputDtoValidator();
        var result = v.Validate(new DeclarationFromSfsInputDto(
            ContributorSqid: "SQID-1",
            ReportingMonth: FirstOfMonth,
            ReferenceNumber: "SFS-001",
            DeclaredContributionAmount: -1m));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>BP 1.2-A — empty reference number is rejected (required for SFS).</summary>
    [Fact]
    public void Sfs_EmptyReference_Fails()
    {
        var v = new DeclarationFromSfsInputDtoValidator();
        var result = v.Validate(new DeclarationFromSfsInputDto(
            ContributorSqid: "SQID-1",
            ReportingMonth: FirstOfMonth,
            ReferenceNumber: string.Empty,
            DeclaredContributionAmount: 1m));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>BP 1.2-B — happy path: BassFour kind passes.</summary>
    [Fact]
    public void Cnas_BassFour_Passes()
    {
        var v = new DeclarationAtCnasInputDtoValidator();
        var result = v.Validate(new DeclarationAtCnasInputDto(
            ContributorSqid: "SQID-1",
            Kind: "BassFour",
            ReportingMonth: FirstOfMonth,
            ReferenceNumber: "FORM-1",
            DeclaredContributionAmount: 100m));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>BP 1.2-B — SFS kind is rejected (must use the SFS endpoint instead).</summary>
    [Fact]
    public void Cnas_SfsKind_Fails()
    {
        var v = new DeclarationAtCnasInputDtoValidator();
        var result = v.Validate(new DeclarationAtCnasInputDto(
            ContributorSqid: "SQID-1",
            Kind: "Sfs",
            ReportingMonth: FirstOfMonth,
            ReferenceNumber: "FORM-1",
            DeclaredContributionAmount: 100m));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>BP 1.2-C — Control kind passes.</summary>
    [Fact]
    public void Other_Control_Passes()
    {
        var v = new DeclarationFromOtherDocumentInputDtoValidator();
        var result = v.Validate(new DeclarationFromOtherDocumentInputDto(
            ContributorSqid: "SQID-1",
            Kind: "Control",
            ReportingMonth: FirstOfMonth,
            ReferenceNumber: null,
            DeclaredContributionAmount: 100m));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>BP 1.2-C — Bass kind is rejected (CNAS-desk endpoint owns that).</summary>
    [Fact]
    public void Other_BassKind_Fails()
    {
        var v = new DeclarationFromOtherDocumentInputDtoValidator();
        var result = v.Validate(new DeclarationFromOtherDocumentInputDto(
            ContributorSqid: "SQID-1",
            Kind: "Bass",
            ReportingMonth: FirstOfMonth,
            ReferenceNumber: null,
            DeclaredContributionAmount: 100m));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>Adjust validator — reason in window passes.</summary>
    [Fact]
    public void Adjust_HappyPath_Passes()
    {
        var v = new DeclarationAdjustInputDtoValidator();
        var result = v.Validate(new DeclarationAdjustInputDto(123m, "Operator correction after audit"));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>Adjust validator — too-short reason fails.</summary>
    [Fact]
    public void Adjust_ShortReason_Fails()
    {
        var v = new DeclarationAdjustInputDtoValidator();
        var result = v.Validate(new DeclarationAdjustInputDto(123m, "ab"));
        result.IsValid.Should().BeFalse();
    }

    // ───────── R0821 — ScannedDeclarationAttachmentInputDtoValidator ─────────

    /// <summary>R0821 — happy path: full envelope with OCR metadata passes.</summary>
    [Fact]
    public void Scanned_HappyPath_Passes()
    {
        var v = new ScannedDeclarationAttachmentInputDtoValidator();
        var result = v.Validate(new ScannedDeclarationAttachmentInputDto(
            FileBase64: "VGVzdA==",
            FileName: "form.pdf",
            ContentType: "application/pdf",
            OcrExtractedJson: "{\"idno\":\"1003600012346\"}",
            OcrConfidenceLevel: "High"));
        result.IsValid.Should().BeTrue();
    }

    /// <summary>R0821 — empty FileBase64 is rejected.</summary>
    [Fact]
    public void Scanned_EmptyFile_Fails()
    {
        var v = new ScannedDeclarationAttachmentInputDtoValidator();
        var result = v.Validate(new ScannedDeclarationAttachmentInputDto(
            FileBase64: string.Empty,
            FileName: "form.pdf"));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>R0821 — OcrExtractedJson exceeding the 100 000-char cap is rejected.</summary>
    [Fact]
    public void Scanned_OversizedOcrJson_Fails()
    {
        var v = new ScannedDeclarationAttachmentInputDtoValidator();
        var oversized = new string('x', 100_001);
        var result = v.Validate(new ScannedDeclarationAttachmentInputDto(
            FileBase64: "VGVzdA==",
            FileName: "form.pdf",
            OcrExtractedJson: oversized));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>R0821 — OcrConfidenceLevel outside the {High, Medium, Low} allow-list is rejected.</summary>
    [Fact]
    public void Scanned_BadConfidenceLevel_Fails()
    {
        var v = new ScannedDeclarationAttachmentInputDtoValidator();
        var result = v.Validate(new ScannedDeclarationAttachmentInputDto(
            FileBase64: "VGVzdA==",
            FileName: "form.pdf",
            OcrConfidenceLevel: "Excellent"));
        result.IsValid.Should().BeFalse();
    }

    // ───────── R0822 — DeclarationsSearchInputValidator ─────────

    /// <summary>R0822 — default page-size passes.</summary>
    [Fact]
    public void Search_DefaultTake_Passes()
    {
        var v = new DeclarationsSearchInputValidator();
        var result = v.Validate(new DeclarationsSearchInput());
        result.IsValid.Should().BeTrue();
    }

    /// <summary>R0822 — Take > 200 is rejected.</summary>
    [Fact]
    public void Search_Take_AbovePageCap_Fails()
    {
        var v = new DeclarationsSearchInputValidator();
        var result = v.Validate(new DeclarationsSearchInput(Take: 500));
        result.IsValid.Should().BeFalse();
    }

    /// <summary>R0822 — negative Skip is rejected.</summary>
    [Fact]
    public void Search_NegativeSkip_Fails()
    {
        var v = new DeclarationsSearchInputValidator();
        var result = v.Validate(new DeclarationsSearchInput(Skip: -1));
        result.IsValid.Should().BeFalse();
    }
}
