using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Validators;

/// <summary>
/// R0322 / TOR UI 014 — validation rules for the application-attachment input
/// shapes. Tests cover the attach payload, the remove-reason payload, the
/// virus-scan-result payload, and the list-filter shape.
/// </summary>
public sealed class ApplicationAttachmentValidatorTests
{
    [Fact]
    public void AttachInput_Baseline_Succeeds()
    {
        var sut = new ApplicationAttachInputDtoValidator();
        var dto = new ApplicationAttachInputDto(
            DocumentSqid: "SQID-1",
            Category: "Identity",
            IsMandatorySnapshot: true,
            Notes: "Carte de identitate scanata");

        var r = sut.Validate(dto);

        r.IsValid.Should().BeTrue(string.Join("; ", r.Errors));
    }

    [Fact]
    public void AttachInput_RejectsUnknownCategory()
    {
        var sut = new ApplicationAttachInputDtoValidator();
        var dto = new ApplicationAttachInputDto(
            DocumentSqid: "SQID-1",
            Category: "Lizard",
            IsMandatorySnapshot: false,
            Notes: null);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(ApplicationAttachInputDto.Category));
    }

    [Fact]
    public void AttachInput_RejectsOversizeNotes()
    {
        var sut = new ApplicationAttachInputDtoValidator();
        var dto = new ApplicationAttachInputDto(
            DocumentSqid: "SQID-1",
            Category: "Income",
            IsMandatorySnapshot: false,
            Notes: new string('x', ApplicationAttachInputDtoValidator.NotesMaxLength + 1));

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == nameof(ApplicationAttachInputDto.Notes));
    }

    [Fact]
    public void Reason_Baseline_Succeeds()
    {
        var sut = new ApplicationAttachmentReasonInputDtoValidator();
        var dto = new ApplicationAttachmentReasonInputDto("Document inlocuit cu varianta corectata");

        var r = sut.Validate(dto);

        r.IsValid.Should().BeTrue(string.Join("; ", r.Errors));
    }

    [Fact]
    public void Reason_RejectsTooShort()
    {
        var sut = new ApplicationAttachmentReasonInputDtoValidator();
        var dto = new ApplicationAttachmentReasonInputDto("ab");

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ScanResult_PendingRejected()
    {
        var sut = new ApplicationAttachmentScanResultInputDtoValidator();
        var dto = new ApplicationAttachmentScanResultInputDto(
            Status: "Pending",
            ScannerName: "clamav-0.103",
            Notes: null);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse(
            "Pending is the row's birth state — only terminal statuses are valid as scan results.");
    }

    [Fact]
    public void ScanResult_CleanAccepted()
    {
        var sut = new ApplicationAttachmentScanResultInputDtoValidator();
        var dto = new ApplicationAttachmentScanResultInputDto(
            Status: "Clean",
            ScannerName: "clamav-0.103",
            Notes: null);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeTrue(string.Join("; ", r.Errors));
    }

    [Fact]
    public void Filter_RejectsNegativeSkip()
    {
        var sut = new ApplicationAttachmentFilterDtoValidator();
        var dto = new ApplicationAttachmentFilterDto(null, null, false, -1, 20);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Filter_RejectsOversizeTake()
    {
        var sut = new ApplicationAttachmentFilterDtoValidator();
        var dto = new ApplicationAttachmentFilterDto(null, null, false, 0, 999);

        var r = sut.Validate(dto);

        r.IsValid.Should().BeFalse();
    }
}
