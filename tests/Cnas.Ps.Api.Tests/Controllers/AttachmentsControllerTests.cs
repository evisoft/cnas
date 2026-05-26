using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0227 / TOR UI 014 — unit tests for <see cref="AttachmentsController"/>. The
/// controller is constructed directly; the underlying <see cref="IAttachmentService"/>
/// is faked with NSubstitute, so the tests are pure HTTP-shape assertions.
/// </summary>
public sealed class AttachmentsControllerTests
{
    [Fact]
    public async Task UploadAsync_ValidBody_Returns201WithSqidPayload()
    {
        var service = Substitute.For<IAttachmentService>();
        var output = SampleDto("SQID-1");
        service.UploadAsync(Arg.Any<AttachmentUploadDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<AttachmentRecordDto>.Success(output));

        var controller = NewController(service);
        var input = new AttachmentUploadDto(
            OwnerEntityType: AttachmentOwnerTypes.ServiceApplication,
            OwnerSqid: "SQID-42",
            ContentBase64: Convert.ToBase64String([0x25, 0x50, 0x44, 0x46, 0x2D]),
            DeclaredFileName: "ok.pdf",
            Category: "Income",
            SensitivityLabel: "Confidential",
            Description: null);

        var result = await controller.UploadAsync(input, CancellationToken.None);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.ActionName.Should().Be(nameof(AttachmentsController.GetAsync));
        created.RouteValues.Should().NotBeNull();
        created.RouteValues!["attachmentSqid"].Should().Be("SQID-1");
        var body = created.Value.Should().BeOfType<AttachmentRecordDto>().Subject;
        body.Id.Should().Be("SQID-1");
    }

    [Fact]
    public async Task DownloadAsync_ServiceReturnsBytes_Returns200FileResult()
    {
        var service = Substitute.For<IAttachmentService>();
        var payload = new AttachmentDownloadDto([0x25, 0x50, 0x44, 0x46], "application/pdf", "x.pdf");
        service.DownloadAsync("SQID-7", Arg.Any<CancellationToken>())
            .Returns(Result<AttachmentDownloadDto>.Success(payload));

        var controller = NewController(service);

        var result = await controller.DownloadAsync("SQID-7", CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/pdf");
        file.FileDownloadName.Should().Be("x.pdf");
        file.FileContents.Should().Equal(payload.Bytes);
    }

    [Fact]
    public async Task GetAsync_NotFound_Returns404()
    {
        var service = Substitute.For<IAttachmentService>();
        service.GetAsync("SQID-missing", Arg.Any<CancellationToken>())
            .Returns(Result<AttachmentRecordDto>.Failure(ErrorCodes.NotFound, "Attachment not found."));

        var controller = NewController(service);

        var result = await controller.GetAsync("SQID-missing", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task UploadAsync_InvalidBody_Returns400()
    {
        var service = Substitute.For<IAttachmentService>();
        var controller = NewController(service);
        var bad = new AttachmentUploadDto(
            OwnerEntityType: "",
            OwnerSqid: "",
            ContentBase64: "",
            DeclaredFileName: "",
            Category: "Income",
            SensitivityLabel: null,
            Description: null);

        var result = await controller.UploadAsync(bad, CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    /// <summary>Builds the standard DTO with the supplied sqid.</summary>
    /// <param name="sqid">Attachment Sqid.</param>
    /// <returns>The DTO.</returns>
    private static AttachmentRecordDto SampleDto(string sqid)
        => new(
            Id: sqid,
            OwnerEntityType: AttachmentOwnerTypes.ServiceApplication,
            OwnerSqid: "SQID-42",
            FileName: "ok.pdf",
            ContentType: "application/pdf",
            SizeBytes: 5,
            Sha256Hex: "deadbeef",
            Category: "Income",
            SensitivityLabel: "Confidential",
            Description: null,
            UploadedByUserSqid: "SQID-100",
            UploadedUtc: DateTime.UtcNow,
            IsArchived: false);

    /// <summary>Builds the controller with the supplied service substitute.</summary>
    /// <param name="service">Mocked service.</param>
    /// <returns>Wired controller.</returns>
    private static AttachmentsController NewController(IAttachmentService service)
        => new(service, new AttachmentUploadDtoValidator());
}
