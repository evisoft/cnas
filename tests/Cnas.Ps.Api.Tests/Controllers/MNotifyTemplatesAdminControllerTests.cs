using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Api.Security;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0115 / TOR CF 14.07 — controller-surface tests for
/// <see cref="MNotifyTemplatesAdminController"/>. Pins the post-split contract
/// where POST means "create" (201 + Location) and PUT means "update"
/// (200 OK on success, 404 on unknown row).
/// </summary>
public sealed class MNotifyTemplatesAdminControllerTests
{
    /// <summary>Reused valid payload across the happy-path tests.</summary>
    private static MNotifyTemplateInputDto ValidInput(string code = "TEMPLATE_X") =>
        new(code, MNotifyChannelKindDto.Email, "Subject", "Body");

    /// <summary>Reused projected DTO for service-stub returns.</summary>
    private static MNotifyTemplateDto Projected(string code = "TEMPLATE_X", string sqid = "SQID-1") =>
        new(sqid, code, MNotifyChannelKindDto.Email, "Subject", "Body",
            IsActive: true,
            UpdatedAtUtc: new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc));

    private static MNotifyTemplatesAdminController CreateController(IMNotifyTemplateService svc) =>
        new(svc, new MNotifyTemplateInputValidator());

    private static ICallbackSignatureVerifier AllowingVerifier()
    {
        var verifier = Substitute.For<ICallbackSignatureVerifier>();
        verifier.Verify(
                CallbackSignatureProvider.MNotify,
                Arg.Any<string>(),
                Arg.Any<IHeaderDictionary>())
            .Returns(CallbackSignatureVerificationResult.Success());
        return verifier;
    }

    private static ICallbackSignatureVerifier RejectingVerifier()
    {
        var verifier = Substitute.For<ICallbackSignatureVerifier>();
        verifier.Verify(
                CallbackSignatureProvider.MNotify,
                Arg.Any<string>(),
                Arg.Any<IHeaderDictionary>())
            .Returns(CallbackSignatureVerificationResult.Failure("signature missing"));
        return verifier;
    }

    /// <summary>
    /// POST a brand-new template returns <c>201 Created</c> with a
    /// <c>Location</c> header pointing at the Sqid-addressed GET route.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NewTemplate_Returns201_WithLocation()
    {
        var svc = Substitute.For<IMNotifyTemplateService>();
        svc.ListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<MNotifyTemplateDto>>.Success(
                Array.Empty<MNotifyTemplateDto>())));
        var persisted = Projected();
        svc.UpsertAsync(Arg.Any<MNotifyTemplateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MNotifyTemplateDto>.Success(persisted)));
        var controller = CreateController(svc);

        var result = await controller.CreateAsync(ValidInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        created.ActionName.Should().Be(nameof(MNotifyTemplatesAdminController.GetAsync));
        created.RouteValues!["sqid"].Should().Be(persisted.Sqid);
        created.Value.Should().BeSameAs(persisted);
    }

    /// <summary>
    /// POST with a payload whose <c>Code</c> collides with an existing row
    /// MUST return <c>409 Conflict</c> — the POST verb's "make new" contract
    /// is honest about duplicate keys instead of silently turning into an
    /// idempotent upsert.
    /// </summary>
    [Fact]
    public async Task CreateAsync_DuplicateCode_Returns409()
    {
        var svc = Substitute.For<IMNotifyTemplateService>();
        var existing = Projected();
        svc.ListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<MNotifyTemplateDto>>.Success(
                new MNotifyTemplateDto[] { existing })));
        var controller = CreateController(svc);

        var result = await controller.CreateAsync(ValidInput(existing.Code), CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        await svc.DidNotReceive().UpsertAsync(
            Arg.Any<MNotifyTemplateInputDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// PUT against a Sqid that exists returns <c>200 OK</c> with the updated
    /// DTO.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ExistingTemplate_Returns200()
    {
        var svc = Substitute.For<IMNotifyTemplateService>();
        var existing = Projected();
        svc.GetAsync(existing.Sqid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MNotifyTemplateDto>.Success(existing)));
        svc.UpsertAsync(Arg.Any<MNotifyTemplateInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MNotifyTemplateDto>.Success(existing)));
        var controller = CreateController(svc);

        var result = await controller.UpdateAsync(existing.Sqid, ValidInput(existing.Code), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value.Should().BeSameAs(existing);
    }

    /// <summary>
    /// PUT against an unknown Sqid returns <c>404 Not Found</c> — distinguishing
    /// "update what's there" from "create new" cleanly at the verb boundary.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_UnknownSqid_Returns404()
    {
        var svc = Substitute.For<IMNotifyTemplateService>();
        svc.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MNotifyTemplateDto>.Failure(
                ErrorCodes.NotFound, "row missing")));
        var controller = CreateController(svc);

        var result = await controller.UpdateAsync("SQID-MISSING", ValidInput(), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        await svc.DidNotReceive().UpsertAsync(
            Arg.Any<MNotifyTemplateInputDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// PUT with a payload whose <c>Code</c> disagrees with the targeted row
    /// returns <c>400 Bad Request</c> — the Sqid is authoritative; mismatched
    /// payload would otherwise create silent mutation of the wrong row.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_CodeMismatch_Returns400()
    {
        var svc = Substitute.For<IMNotifyTemplateService>();
        var existing = Projected(code: "REAL_CODE");
        svc.GetAsync(existing.Sqid, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<MNotifyTemplateDto>.Success(existing)));
        var controller = CreateController(svc);

        var result = await controller.UpdateAsync(existing.Sqid, ValidInput("WRONG_CODE"), CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await svc.DidNotReceive().UpsertAsync(
            Arg.Any<MNotifyTemplateInputDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BounceAsync_MissingValidSignature_Returns401WithoutCallingHandler()
    {
        var handler = Substitute.For<IMNotifyBounceHandler>();
        var controller = new MNotifyBounceWebhookController(handler, RejectingVerifier())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        var payload = new MNotifyBounceWebhookPayload(
            NotificationReference: "MNOTIFY-1",
            BounceCode: "hard-bounce",
            BounceReason: "mailbox unavailable",
            OccurredAtUtc: new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc));

        var result = await controller.BounceAsync(payload, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        await handler.DidNotReceiveWithAnyArgs().HandleBounceAsync(default!, default);
    }

    [Fact]
    public async Task BounceAsync_ValidSignature_CallsHandler()
    {
        var handler = Substitute.For<IMNotifyBounceHandler>();
        handler.HandleBounceAsync(Arg.Any<MNotifyBounceWebhookPayload>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        var controller = new MNotifyBounceWebhookController(handler, AllowingVerifier())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        var payload = new MNotifyBounceWebhookPayload(
            NotificationReference: "MNOTIFY-1",
            BounceCode: "hard-bounce",
            BounceReason: "mailbox unavailable",
            OccurredAtUtc: new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc));

        var result = await controller.BounceAsync(payload, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        await handler.Received(1).HandleBounceAsync(payload, Arg.Any<CancellationToken>());
    }
}
