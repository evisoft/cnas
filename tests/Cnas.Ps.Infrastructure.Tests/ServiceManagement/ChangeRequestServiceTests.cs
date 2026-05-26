using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.ServiceManagement;

namespace Cnas.Ps.Infrastructure.Tests.ServiceManagement;

/// <summary>
/// R2505 / TOR PIR 030-033 — tests for
/// <see cref="ChangeRequestService"/>.
/// </summary>
public sealed class ChangeRequestServiceTests
{
    private static ChangeRequestService NewService(
        CnasDbContext db,
        IAuditService audit,
        ICallerContext? caller = null,
        DateTime? now = null)
        => new(
            db: db,
            read: db,
            clock: new ServiceManagementTestHelpers.StubClock(now ?? ServiceManagementTestHelpers.ClockNow),
            sqids: ServiceManagementTestHelpers.NewSqidMock(),
            caller: caller ?? ServiceManagementTestHelpers.NewCaller(),
            audit: audit,
            createValidator: new ChangeRequestCreateInputValidator(),
            testValidator: new ChangeRequestTestValidationInputValidator(),
            signValidator: new ChangeRequestSignCodeInputValidator(),
            rollbackValidator: new ChangeRequestRollbackInputValidator(),
            reasonValidator: new ChangeRequestReasonInputValidator(),
            filterValidator: new ChangeRequestFilterValidator());

    private static ChangeRequestCreateInputDto NewCreateDto()
        => new(
            Title: "Patch authentication library",
            Description: "Upgrade the in-house auth library to mitigate CVE-2026-12345. Roll out in low-traffic window.",
            Kind: "Normal",
            Risk: "Medium",
            ImpactedSystems: "auth-api, web-portal",
            RollbackPlan: "Re-deploy the previous container tag and restore the previous signing key from the vault.",
            RelatedMaintenanceWindowSqid: null);

    [Fact]
    public async Task Create_HappyPath_Succeeds_AndEmitsAudit()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);
        var svc = NewService(db, audit);

        var result = await svc.CreateAsync(NewCreateDto(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ChangeNumber.Should().StartWith("CHG-");
        result.Value.Status.Should().Be(nameof(ChangeRequestStatus.Draft));
        codes.Should().Contain(IChangeRequestService.AuditCreated);
    }

    [Fact]
    public async Task Submit_ShortRollbackPlan_RejectedByValidator()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = NewService(db, audit);

        var bad = NewCreateDto() with { RollbackPlan = "Revert." };
        var result = await svc.CreateAsync(bad, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task ValidateTestEnv_BySameUser_ReturnsSameOperator()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = NewService(db, audit);

        // Created by USR-1.
        var created = await svc.CreateAsync(NewCreateDto(), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        var sqid = created.Value!.Id;
        (await svc.SubmitAsync(sqid, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await svc.StartReviewAsync(sqid, CancellationToken.None)).IsSuccess.Should().BeTrue();

        // Same operator (USR-1 = requester).
        var result = await svc.ValidateTestEnvAsync(
            sqid,
            new ChangeRequestTestValidationInputDto("Validated in test env, results green."),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ChangeRequestSameOperator);
    }

    [Fact]
    public async Task SignCode_ByTesterOrRequester_ReturnsSameOperator()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);

        // Requester USR-1
        var requesterCaller = ServiceManagementTestHelpers.NewCallerWith(1L, "USR-1");
        var svcReq = NewService(db, audit, requesterCaller);
        var created = await svcReq.CreateAsync(NewCreateDto(), CancellationToken.None);
        var sqid = created.Value!.Id;
        (await svcReq.SubmitAsync(sqid, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await svcReq.StartReviewAsync(sqid, CancellationToken.None)).IsSuccess.Should().BeTrue();

        // Tester USR-2 validates test-env (distinct from requester USR-1).
        var testerCaller = ServiceManagementTestHelpers.NewCallerWith(2L, "USR-2");
        var svcTester = NewService(db, audit, testerCaller);
        (await svcTester.ValidateTestEnvAsync(
            sqid,
            new ChangeRequestTestValidationInputDto("Validated in test environment by the test team."),
            CancellationToken.None)).IsSuccess.Should().BeTrue();

        // Now the tester tries to sign — must fail.
        var resultByTester = await svcTester.SignCodeAsync(
            sqid,
            new ChangeRequestSignCodeInputDto("sha256:abc"),
            CancellationToken.None);
        resultByTester.IsFailure.Should().BeTrue();
        resultByTester.ErrorCode.Should().Be(ErrorCodes.ChangeRequestSameOperator);

        // And the requester also cannot sign.
        var resultByRequester = await svcReq.SignCodeAsync(
            sqid,
            new ChangeRequestSignCodeInputDto("sha256:abc"),
            CancellationToken.None);
        resultByRequester.IsFailure.Should().BeTrue();
        resultByRequester.ErrorCode.Should().Be(ErrorCodes.ChangeRequestSameOperator);
    }

    [Fact]
    public async Task Approve_ByPriorActor_ReturnsSameOperator()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);

        var requester = ServiceManagementTestHelpers.NewCallerWith(1L, "USR-1");
        var tester = ServiceManagementTestHelpers.NewCallerWith(2L, "USR-2");
        var signer = ServiceManagementTestHelpers.NewCallerWith(3L, "USR-3");

        var svcReq = NewService(db, audit, requester);
        var created = await svcReq.CreateAsync(NewCreateDto(), CancellationToken.None);
        var sqid = created.Value!.Id;
        await svcReq.SubmitAsync(sqid, CancellationToken.None);
        await svcReq.StartReviewAsync(sqid, CancellationToken.None);

        var svcTester = NewService(db, audit, tester);
        await svcTester.ValidateTestEnvAsync(
            sqid, new ChangeRequestTestValidationInputDto("Tested, all green."), CancellationToken.None);

        var svcSigner = NewService(db, audit, signer);
        await svcSigner.SignCodeAsync(sqid, new ChangeRequestSignCodeInputDto("sha256:xyz"), CancellationToken.None);

        // Tester approves → conflict.
        var approveByTester = await svcTester.ApproveAsync(sqid, CancellationToken.None);
        approveByTester.IsFailure.Should().BeTrue();
        approveByTester.ErrorCode.Should().Be(ErrorCodes.ChangeRequestSameOperator);

        // Signer approves → conflict.
        var approveBySigner = await svcSigner.ApproveAsync(sqid, CancellationToken.None);
        approveBySigner.IsFailure.Should().BeTrue();
        approveBySigner.ErrorCode.Should().Be(ErrorCodes.ChangeRequestSameOperator);

        // Requester approves → conflict.
        var approveByReq = await svcReq.ApproveAsync(sqid, CancellationToken.None);
        approveByReq.IsFailure.Should().BeTrue();
        approveByReq.ErrorCode.Should().Be(ErrorCodes.ChangeRequestSameOperator);

        // A distinct approver succeeds.
        var approver = ServiceManagementTestHelpers.NewCallerWith(4L, "USR-4");
        var svcApprover = NewService(db, audit, approver);
        var ok = await svcApprover.ApproveAsync(sqid, CancellationToken.None);
        ok.IsSuccess.Should().BeTrue();
        ok.Value!.Status.Should().Be(nameof(ChangeRequestStatus.ApprovedForProd));
    }

    [Fact]
    public async Task Rollback_FromDeployed_Succeeds_AndAudits()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out var codes);

        var requester = ServiceManagementTestHelpers.NewCallerWith(1L, "USR-1");
        var tester = ServiceManagementTestHelpers.NewCallerWith(2L, "USR-2");
        var signer = ServiceManagementTestHelpers.NewCallerWith(3L, "USR-3");
        var approver = ServiceManagementTestHelpers.NewCallerWith(4L, "USR-4");

        var svcReq = NewService(db, audit, requester);
        var created = await svcReq.CreateAsync(NewCreateDto(), CancellationToken.None);
        var sqid = created.Value!.Id;
        await svcReq.SubmitAsync(sqid, CancellationToken.None);
        await svcReq.StartReviewAsync(sqid, CancellationToken.None);
        await NewService(db, audit, tester).ValidateTestEnvAsync(
            sqid, new ChangeRequestTestValidationInputDto("Test env validated by the test team."), CancellationToken.None);
        await NewService(db, audit, signer).SignCodeAsync(
            sqid, new ChangeRequestSignCodeInputDto("sha256:xyz"), CancellationToken.None);
        await NewService(db, audit, approver).ApproveAsync(sqid, CancellationToken.None);
        await NewService(db, audit, approver).StartDeploymentAsync(sqid, CancellationToken.None);
        await NewService(db, audit, approver).CompleteDeploymentAsync(sqid, CancellationToken.None);

        var rollback = await NewService(db, audit, approver).RollBackAsync(
            sqid,
            new ChangeRequestRollbackInputDto("Production smoke test failed; restoring prior version."),
            CancellationToken.None);

        rollback.IsSuccess.Should().BeTrue();
        rollback.Value!.Status.Should().Be(nameof(ChangeRequestStatus.RolledBack));
        codes.Should().Contain(IChangeRequestService.AuditRolledBack);
    }

    [Fact]
    public async Task ChangeNumber_AutoMinted_Pattern_Matches()
    {
        using var db = ServiceManagementTestHelpers.CreateContext();
        var audit = ServiceManagementTestHelpers.NewAuditCapturing(out _);
        var svc = NewService(db, audit);

        var created = await svc.CreateAsync(NewCreateDto(), CancellationToken.None);

        created.IsSuccess.Should().BeTrue();
        // Format: CHG-{year}-{seq:000000}; first row of the year → seq 1.
        created.Value!.ChangeNumber.Should().MatchRegex(@"^CHG-\d{4}-\d{6}$");
    }
}
