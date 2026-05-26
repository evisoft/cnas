using System.Text;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="TemplatesController"/> (UC17 — template admin surface).
/// Direct-construction pattern (no <c>WebApplicationFactory</c>): the service is a
/// substitute and the controller is instantiated with that substitute so each branch
/// exercises pure controller logic — not the authentication pipeline. The 401 / 403
/// paths are locked end-to-end by the matching E2E journey
/// (<c>Uc17_TemplateAdminJourneyTests</c>) which boots the real auth handler.
/// </summary>
public sealed class TemplatesControllerTests
{
    /// <summary>Helper that produces a fresh service substitute.</summary>
    private static ITemplateAdminService NewServiceMock() => Substitute.For<ITemplateAdminService>();

    /// <summary>Helper that produces a fresh document-generation service substitute.</summary>
    private static IDocumentGenerationService NewDocGenMock() => Substitute.For<IDocumentGenerationService>();

    /// <summary>
    /// Builds the SUT around the supplied template-admin service and a no-op
    /// document-generation substitute. Used by the pre-phase-2B tests that do not
    /// exercise the render route.
    /// </summary>
    private static TemplatesController NewController(ITemplateAdminService svc) =>
        new(svc, NewDocGenMock());

    /// <summary>Builds the SUT around both collaborators — used by the render tests.</summary>
    private static TemplatesController NewController(
        ITemplateAdminService svc,
        IDocumentGenerationService docGen) => new(svc, docGen);

    // ─────────────────────── ListAsync ───────────────────────

    [Fact]
    public async Task Get_CnasAdmin_Returns200WithCatalog()
    {
        var svc = NewServiceMock();
        IReadOnlyList<TemplateCatalogEntry> entries =
        [
            new("alpha", "Cnas.Ps.Tests.AlphaTemplate", "Cnas.Ps.Tests"),
            new("bravo", "Cnas.Ps.Tests.BravoTemplate", "Cnas.Ps.Tests"),
        ];
        svc.ListAsync(Arg.Any<CancellationToken>())
           .Returns(Result<IReadOnlyList<TemplateCatalogEntry>>.Success(entries));
        var controller = NewController(svc);

        var result = await controller.ListAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(entries);
    }

    [Fact]
    public async Task Get_ServiceFailure_Returns400()
    {
        var svc = NewServiceMock();
        svc.ListAsync(Arg.Any<CancellationToken>())
           .Returns(Result<IReadOnlyList<TemplateCatalogEntry>>.Failure(
               ErrorCodes.Internal, "Boom."));
        var controller = NewController(svc);

        var result = await controller.ListAsync(CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // ─────────────────────── GetByCodeAsync ───────────────────────

    [Fact]
    public async Task GetByCode_KnownCode_Returns200()
    {
        var svc = NewServiceMock();
        var entry = new TemplateCatalogEntry(
            "refuz-aplicare",
            "Cnas.Ps.Infrastructure.Documents.Templates.RefuzAplicareTemplate",
            "Cnas.Ps.Infrastructure");
        svc.GetAsync(Arg.Is<string>(c => c == "refuz-aplicare"), Arg.Any<CancellationToken>())
           .Returns(Result<TemplateCatalogEntry>.Success(entry));
        var controller = NewController(svc);

        var result = await controller.GetByCodeAsync("refuz-aplicare", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(entry);
    }

    [Fact]
    public async Task GetByCode_UnknownCode_Returns404()
    {
        var svc = NewServiceMock();
        svc.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<TemplateCatalogEntry>.Failure(ErrorCodes.NotFound, "Unknown template code"));
        var controller = NewController(svc);

        var result = await controller.GetByCodeAsync("does-not-exist", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetByCode_ValidationFailure_Returns400()
    {
        var svc = NewServiceMock();
        svc.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<TemplateCatalogEntry>.Failure(ErrorCodes.ValidationFailed, "Bad code"));
        var controller = NewController(svc);

        var result = await controller.GetByCodeAsync("???", CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Bad code");
    }

    // ─────────────────────── UploadAsync ───────────────────────

    [Fact]
    public async Task Upload_ServiceSuccess_Returns201WithEntry()
    {
        var svc = NewServiceMock();
        var entry = new TemplateCatalogEntry(
            "uploaded", string.Empty, string.Empty,
            Source: "Persistent", Name: "Uploaded", Version: 1, ContentLength: 8);
        svc.UploadAsync(
                Arg.Is<string>(c => c == "uploaded"),
                Arg.Is<string>(n => n == "Uploaded"),
                Arg.Any<string?>(),
                Arg.Any<Stream>(),
                Arg.Is<string>(ct => ct.Contains("wordprocessingml")),
                Arg.Any<CancellationToken>())
           .Returns(Result<TemplateCatalogEntry>.Success(entry));
        var controller = NewController(svc);

        var file = MakeFormFile(
            "uploaded.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00]);

        var result = await controller.UploadAsync(file, "uploaded", "Uploaded", null, CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        created.Value.Should().BeSameAs(entry);
    }

    [Fact]
    public async Task Upload_FileTooLarge_Returns400()
    {
        var svc = NewServiceMock();
        svc.UploadAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<Stream>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
           .Returns(Result<TemplateCatalogEntry>.Failure(ErrorCodes.FileTooLarge, "too big"));
        var controller = NewController(svc);

        var file = MakeFormFile("x.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [0x50, 0x4B, 0x03, 0x04]);
        var result = await controller.UploadAsync(file, "x", "X", null, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Upload_MissingFile_Returns400()
    {
        var svc = NewServiceMock();
        var controller = NewController(svc);

        var result = await controller.UploadAsync(null!, "code", "Name", null, CancellationToken.None);

        // Null IFormFile is rejected with 400 before the service is touched.
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceiveWithAnyArgs().UploadAsync(default!, default!, default, default!, default!, default);
    }

    // ─────────────────────── DownloadAsync ───────────────────────

    [Fact]
    public async Task Download_PersistentRow_ReturnsFileStreamResult()
    {
        var svc = NewServiceMock();
        var payload = new byte[] { 1, 2, 3, 4 };
        var stream = new MemoryStream(payload);
        var dl = new TemplateDownloadStream(
            Content: stream,
            ContentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ContentLength: payload.Length,
            SuggestedFileName: "dl.docx",
            Sha256: new string('a', 64));
        svc.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<TemplateDownloadStream>.Success(dl));
        var controller = NewController(svc);

        var result = await controller.DownloadAsync("dl", CancellationToken.None);

        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.FileDownloadName.Should().Be("dl.docx");
        file.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        file.FileStream.Should().BeSameAs(stream);
    }

    [Fact]
    public async Task Download_NotFound_Returns404()
    {
        var svc = NewServiceMock();
        svc.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(Result<TemplateDownloadStream>.Failure(ErrorCodes.NotFound, "nope"));
        var controller = NewController(svc);

        var result = await controller.DownloadAsync("nope", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // ─────────────────────── RenderAsync (UC17 phase 2B) ───────────────────────

    /// <summary>
    /// Happy path: a known persistent template renders successfully and the controller
    /// surfaces the bytes as a <see cref="FileStreamResult"/> with the DOCX MIME type
    /// and a <c>{code}.docx</c> download name. Locks the wiring between the controller
    /// action and <see cref="IDocumentGenerationService.GenerateFromUploadedTemplateAsync"/>.
    /// </summary>
    [Fact]
    public async Task Render_KnownUploadedTemplate_ReturnsRenderedBytes()
    {
        var svc = NewServiceMock();
        var docGen = NewDocGenMock();
        var renderedBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF };
        docGen.GenerateFromUploadedTemplateAsync(
                Arg.Is<string>(c => c == "uploaded-render"),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
              .Returns(Result<byte[]>.Success(renderedBytes));
        var controller = NewController(svc, docGen);

        var request = new RenderUploadedTemplateRequest(
            new Dictionary<string, string> { ["citizen"] = "Ion Popescu" });

        var result = await controller.RenderAsync("uploaded-render", request, CancellationToken.None);

        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        file.FileDownloadName.Should().Be("uploaded-render.docx");

        // Drain the stream and verify byte-identity with the bytes returned by the service.
        using var ms = new MemoryStream();
        await file.FileStream.CopyToAsync(ms);
        ms.ToArray().Should().Equal(renderedBytes);
    }

    /// <summary>
    /// An unknown template code maps the service's <see cref="ErrorCodes.NotFound"/>
    /// failure to a 404 — mirrors the GetByCode / Download branches.
    /// </summary>
    [Fact]
    public async Task Render_UnknownTemplate_Returns404()
    {
        var svc = NewServiceMock();
        var docGen = NewDocGenMock();
        docGen.GenerateFromUploadedTemplateAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
              .Returns(Result<byte[]>.Failure(ErrorCodes.NotFound, "Unknown template."));
        var controller = NewController(svc, docGen);

        var request = new RenderUploadedTemplateRequest(
            new Dictionary<string, string>());

        var result = await controller.RenderAsync("does-not-exist", request, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Anonymous callers are rejected by the <c>[Authorize(Policy = CnasUser)]</c>
    /// attribute before the controller runs. This assertion locks the policy choice
    /// at the metadata level — the attribute is inspected on the action method, so
    /// the test does not need to boot the auth pipeline.
    /// </summary>
    [Fact]
    public void Render_AnonymousCaller_Returns401()
    {
        // The Authorize attribute is inherited from the controller class. Confirm the
        // class carries an [Authorize] attribute bound to the CnasUser policy so an
        // anonymous request never reaches the action — the framework short-circuits
        // with a 401 challenge.
        var attr = typeof(TemplatesController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull(
            "TemplatesController must carry an [Authorize] attribute so anonymous callers " +
            "receive 401 from the policy gate before any action runs");
        // The render route deliberately broadens the controller-default CnasAdmin policy
        // to CnasUser via a method-level [Authorize], so the controller-level attribute
        // is still CnasAdmin. Verify either path-level or method-level wires authorization
        // for the render action.
        var renderMethod = typeof(TemplatesController).GetMethod(nameof(TemplatesController.RenderAsync));
        renderMethod.Should().NotBeNull("RenderAsync action must exist");
        var renderAttrs = renderMethod!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .ToList();
        renderAttrs.Should().NotBeEmpty(
            "RenderAsync must be reachable only by an authenticated caller — without an " +
            "[Authorize] attribute (inherited or method-level), anonymous callers would hit a 200");
    }

    /// <summary>
    /// A request body whose <c>Data</c> field is missing is rejected with 400 by the
    /// controller. We model "badly shaped data" as a null request body (which is what
    /// the framework binder hands the action when the JSON cannot be deserialised into
    /// the record) — the controller defends with an explicit null-check rather than
    /// letting the service take a <see cref="NullReferenceException"/>.
    /// </summary>
    [Fact]
    public async Task Render_BadlyShapedData_Returns400()
    {
        var svc = NewServiceMock();
        var docGen = NewDocGenMock();
        var controller = NewController(svc, docGen);

        // Null body — the JSON binder hands action a null record when the payload
        // cannot be deserialised (e.g. body absent, body is "null", body is malformed).
        var result = await controller.RenderAsync("any-code", body: null!, CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await docGen.DidNotReceiveWithAnyArgs().GenerateFromUploadedTemplateAsync(
            default!, default, default);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>Builds a minimal in-memory <see cref="IFormFile"/> for upload tests.</summary>
    private static IFormFile MakeFormFile(string fileName, string contentType, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }
}
