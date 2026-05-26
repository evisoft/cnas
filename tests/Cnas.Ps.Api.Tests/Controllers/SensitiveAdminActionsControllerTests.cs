using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.SensitiveActions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R2273 / TOR SEC 027 — tests for <see cref="SensitiveAdminActionsController"/>.
/// Verifies the cnas-admin authorize gate, the request happy-path, the approve happy
/// path, the reject happy path, and the registry endpoint.
/// </summary>
public sealed class SensitiveAdminActionsControllerTests
{
    private const string SqidA = "SQID-1";

    private static SensitiveAdminActionDto MakeDto(string status = "PendingApproval")
        => new(
            Id: SqidA,
            ActionCode: "USER.ROLE_GRANT",
            Status: status,
            RequestedByUserSqid: "SQID-100",
            RequestedAt: DateTime.UtcNow,
            RequestReason: "Reason",
            RequestPayloadJson: "{}",
            ApprovedByUserSqid: null,
            ApprovedAt: null,
            ApprovalNote: null,
            RejectedByUserSqid: null,
            RejectedAt: null,
            RejectionReason: null,
            CancelledAt: null,
            CancelReason: null,
            ExpiresAt: DateTime.UtcNow.AddHours(72),
            ExecutedAt: null,
            ExecutionResultJson: null,
            ExecutionFailureReason: null);

    [Fact]
    public void Controller_HasCnasAdminAuthorizationPolicy()
    {
        var attrs = typeof(SensitiveAdminActionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .ToList();
        attrs.Should().NotBeEmpty();
        attrs.Should().Contain(a => a.Policy == AuthorizationComposition.CnasAdmin);
    }

    [Fact]
    public async Task Request_HappyPath_Returns201()
    {
        var dto = MakeDto();
        var svc = Substitute.For<ISensitiveAdminActionService>();
        svc.RequestAsync(
                Arg.Any<SensitiveAdminActionRequestInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SensitiveAdminActionDto>.Success(dto)));
        var registry = Substitute.For<ISensitiveActionRegistry>();

        var controller = new SensitiveAdminActionsController(svc, registry);
        var result = await controller.RequestAsync(
            new SensitiveAdminActionRequestInputDto(
                ActionCode: "USER.ROLE_GRANT",
                RequestReason: "Test reason that is long enough.",
                RequestPayloadJson: "{\"role\":\"cnas-admin\"}"),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedResult>().Subject;
        created.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task Approve_HappyPath_Returns200()
    {
        var dto = MakeDto("Approved");
        var svc = Substitute.For<ISensitiveAdminActionService>();
        svc.ApproveAsync(
                SqidA,
                Arg.Any<SensitiveAdminActionApprovalInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SensitiveAdminActionDto>.Success(dto)));
        var registry = Substitute.For<ISensitiveActionRegistry>();

        var controller = new SensitiveAdminActionsController(svc, registry);
        var result = await controller.ApproveAsync(
            SqidA,
            new SensitiveAdminActionApprovalInputDto("Approving for the test."),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task Reject_HappyPath_Returns200()
    {
        var dto = MakeDto("Rejected");
        var svc = Substitute.For<ISensitiveAdminActionService>();
        svc.RejectAsync(
                SqidA,
                Arg.Any<SensitiveAdminActionReasonInputDto>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<SensitiveAdminActionDto>.Success(dto)));
        var registry = Substitute.For<ISensitiveActionRegistry>();

        var controller = new SensitiveAdminActionsController(svc, registry);
        var result = await controller.RejectAsync(
            SqidA,
            new SensitiveAdminActionReasonInputDto("Rejecting — incorrect target user."),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public void Registry_ReturnsRegisteredPolicies()
    {
        var entries = new SensitiveActionRegistryEntryDto[]
        {
            new("USER.ROLE_GRANT", "Grant role", 48.0),
            new("USER.ROLE_REVOKE", "Revoke role", null),
        };
        var svc = Substitute.For<ISensitiveAdminActionService>();
        var registry = Substitute.For<ISensitiveActionRegistry>();
        registry.Describe().Returns(entries);

        var controller = new SensitiveAdminActionsController(svc, registry);
        var result = controller.GetRegistry();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(entries);
    }
}
