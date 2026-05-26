using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Direct-construction unit tests for <see cref="ExaminationController"/>. The branch logic
/// is pure; only the <see cref="IDocumentExaminationService"/> dependency needs a stub.
/// </summary>
public sealed class ExaminationControllerTests
{
    private static IDocumentExaminationService NewServiceMock() => Substitute.For<IDocumentExaminationService>();

    [Fact]
    public async Task RecordVerdict_HappyPath_Returns200()
    {
        var svc = NewServiceMock();
        svc.RecordVerdictAsync(
                Arg.Any<string>(),
                ExaminationVerdict.Accepted,
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        var controller = new ExaminationController(svc);

        var result = await controller.RecordVerdictAsync(
            "DOC-SQID",
            new VerdictRequest("Accepted", "all good"),
            CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task GenerateDrafts_HappyPath_Returns200WithBothIds()
    {
        var svc = NewServiceMock();
        var draft = new DraftDocumentsResult("SHEET-SQID", "DEC-SQID");
        svc.GenerateDraftsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<DraftDocumentsResult>.Success(draft)));
        var controller = new ExaminationController(svc);

        var result = await controller.GenerateDraftsAsync("DOSS-SQID", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(draft);
    }

    [Fact]
    public async Task SubmitForApproval_HappyPath_Returns200()
    {
        var svc = NewServiceMock();
        svc.SubmitForApprovalAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        var controller = new ExaminationController(svc);

        var result = await controller.SubmitForApprovalAsync("DOSS-SQID", CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Refuse_BadRequest_Returns400()
    {
        var svc = NewServiceMock();
        var controller = new ExaminationController(svc);

        // Empty reason — controller should reject with 400 before invoking the service.
        var result = await controller.RefuseAsync(
            "DOSS-SQID",
            new RefuseRequest(string.Empty),
            CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);

        await svc.DidNotReceiveWithAnyArgs().RefuseAsync(default!, default!, default);
    }

    // ─────────────────────── EmitNewDecisionAsync (R0573) ───────────────────────

    [Fact]
    public async Task EmitNewDecision_HappyPath_Returns200WithEmittedDto()
    {
        var svc = NewServiceMock();
        var emitted = new EmittedDecisionDto("NEW-DOC-SQID", "decizia-pensie");
        svc.EmitNewDecisionAsync(
                Arg.Any<string>(),
                Arg.Any<EmitNewDecisionInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<EmittedDecisionDto>.Success(emitted)));
        var controller = new ExaminationController(svc);

        var result = await controller.EmitNewDecisionAsync(
            "DOSS-SQID",
            new EmitNewDecisionInputDto("decizia-pensie", "ok", null),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(emitted);
    }

    [Fact]
    public async Task EmitNewDecision_ValidationFails_Returns400()
    {
        var svc = NewServiceMock();
        var controller = new ExaminationController(svc);

        // Empty template code — validator rejects with 400 before the service is invoked.
        var result = await controller.EmitNewDecisionAsync(
            "DOSS-SQID",
            new EmitNewDecisionInputDto(string.Empty, null, null),
            CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);

        await svc.DidNotReceiveWithAnyArgs().EmitNewDecisionAsync(default!, default!, default);
    }

    [Fact]
    public async Task EmitNewDecision_NotEditable_Returns409()
    {
        var svc = NewServiceMock();
        svc.EmitNewDecisionAsync(
                Arg.Any<string>(),
                Arg.Any<EmitNewDecisionInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<EmittedDecisionDto>.Failure(
                ErrorCodes.ExaminationNotEditable, "terminal")));
        var controller = new ExaminationController(svc);

        var result = await controller.EmitNewDecisionAsync(
            "DOSS-SQID",
            new EmitNewDecisionInputDto("decizia-pensie", null, null),
            CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task EmitNewDecision_TemplateNotFound_Returns404()
    {
        var svc = NewServiceMock();
        svc.EmitNewDecisionAsync(
                Arg.Any<string>(),
                Arg.Any<EmitNewDecisionInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<EmittedDecisionDto>.Failure(
                ErrorCodes.DocumentTemplateNotFound, "unknown template")));
        var controller = new ExaminationController(svc);

        var result = await controller.EmitNewDecisionAsync(
            "DOSS-SQID",
            new EmitNewDecisionInputDto("not-real", null, null),
            CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(404);
    }
}
