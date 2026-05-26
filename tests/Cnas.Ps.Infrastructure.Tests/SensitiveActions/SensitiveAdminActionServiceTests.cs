using System.Diagnostics.Metrics;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Services.SensitiveActions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — tests for <see cref="SensitiveAdminActionService"/>. Covers
/// the request happy-path, the same-operator guard, the no-handler-registered path,
/// the registered-handler path, the cancel-after-approve conflict, and the expiry
/// sweep.
/// </summary>
public sealed class SensitiveAdminActionServiceTests
{
    private const string FakeActionCode = SensitiveActionsTestHelpers.FakeUserStateChangePolicy.Code;
    private const long Requester = 100L;
    private const long Approver = 200L;

    private static SensitiveAdminActionRequestInputDto ValidRequest()
        => new(
            ActionCode: FakeActionCode,
            RequestReason: "Suspending the account because the user left the company.",
            RequestPayloadJson: "{\"targetUserSqid\":\"SQID-7\",\"newState\":\"Suspended\"}");

    [Fact]
    public async Task Request_HappyPath_PersistsAndEmitsAuditAndMetric()
    {
        // Snapshot the meter via MeterListener — process-static state.
        var observed = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instr, l) =>
        {
            if (instr.Meter.Name == CnasMeter.MeterName && instr.Name == "cnas.sensitive_admin_action.requested")
            {
                l.EnableMeasurementEvents(instr);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref observed, value));
        listener.Start();

        using var db = SensitiveActionsTestHelpers.CreateContext();
        var caller = SensitiveActionsTestHelpers.NewCaller(Requester);
        var audit = SensitiveActionsTestHelpers.NewAudit();
        var svc = SensitiveActionsTestHelpers.NewService(
            db, caller, audit,
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() });

        var result = await svc.RequestAsync(ValidRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var row = await db.SensitiveAdminActions.SingleAsync();
        row.Status.Should().Be(SensitiveAdminActionStatus.PendingApproval);
        row.RequestedByUserId.Should().Be(Requester);
        // 48h override from the fake policy — verifies the policy expiration plumbing.
        row.ExpiresAt.Should().Be(SensitiveActionsTestHelpers.ClockNow + TimeSpan.FromHours(48));

        await audit.Received().RecordAsync(
            "SENS_ADMIN.REQUESTED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(SensitiveAdminAction),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        observed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Request_NoPolicyOverride_UsesDefault72hWindow()
    {
        using var db = SensitiveActionsTestHelpers.CreateContext();
        var caller = SensitiveActionsTestHelpers.NewCaller(Requester);
        // Use a different ActionCode whose policy has no expiration override.
        var nopExpirationPolicy = new InlineFakePolicy("OTHER.OP");
        var svc = SensitiveActionsTestHelpers.NewService(db, caller,
            policies: new[] { (Cnas.Ps.Application.SensitiveActions.ISensitiveActionPolicy)nopExpirationPolicy });

        var input = ValidRequest() with { ActionCode = "OTHER.OP" };
        var result = await svc.RequestAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var row = await db.SensitiveAdminActions.SingleAsync();
        row.ExpiresAt.Should().Be(SensitiveActionsTestHelpers.ClockNow + SensitiveAdminActionService.DefaultExpirationWindow);
    }

    [Fact]
    public async Task Approve_SameOperator_ReturnsFourEyesConflict()
    {
        using var db = SensitiveActionsTestHelpers.CreateContext();
        var caller = SensitiveActionsTestHelpers.NewCaller(Requester);
        var svc = SensitiveActionsTestHelpers.NewService(db, caller,
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() });
        var created = await svc.RequestAsync(ValidRequest(), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        // Approver is the SAME operator as the requester — must be rejected.
        var result = await svc.ApproveAsync(
            created.Value.Id,
            new SensitiveAdminActionApprovalInputDto("Approving my own request."),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.FourEyesSameOperator);
    }

    [Fact]
    public async Task Approve_NoHandler_RecordsExecutionFailedNoHandler()
    {
        using var db = SensitiveActionsTestHelpers.CreateContext();
        var requesterCaller = SensitiveActionsTestHelpers.NewCaller(Requester);
        var requesterSvc = SensitiveActionsTestHelpers.NewService(
            db, requesterCaller,
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() },
            handlers: null /* no handler registered */);
        var created = await requesterSvc.RequestAsync(ValidRequest(), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        // Switch to a distinct approver caller for the approve call.
        var approverCaller = SensitiveActionsTestHelpers.NewCaller(Approver);
        var approverAudit = SensitiveActionsTestHelpers.NewAudit();
        var approverSvc = SensitiveActionsTestHelpers.NewService(
            db, approverCaller, approverAudit,
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() },
            handlers: null);
        var result = await approverSvc.ApproveAsync(
            created.Value.Id,
            new SensitiveAdminActionApprovalInputDto("Approving — handler stubbed for test."),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var row = await db.SensitiveAdminActions.SingleAsync();
        row.Status.Should().Be(SensitiveAdminActionStatus.ExecutionFailed);
        row.ExecutionFailureReason.Should().Be(SensitiveAdminActionService.NoHandlerRegistered);

        // Critical audit MUST fire for the approval AND the execution-failed transition.
        await approverAudit.Received().RecordAsync(
            "SENS_ADMIN.APPROVED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(SensitiveAdminAction),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await approverAudit.Received().RecordAsync(
            "SENS_ADMIN.EXECUTION_FAILED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(SensitiveAdminAction),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_WithHandler_FlipsToExecutedAndRecordsResult()
    {
        using var db = SensitiveActionsTestHelpers.CreateContext();
        var requesterSvc = SensitiveActionsTestHelpers.NewService(
            db, SensitiveActionsTestHelpers.NewCaller(Requester),
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() },
            handlers: new[] { (Cnas.Ps.Application.SensitiveActions.ISensitiveActionHandler)new SensitiveActionsTestHelpers.FakeUserStateChangeHandler() });
        var created = await requesterSvc.RequestAsync(ValidRequest(), CancellationToken.None);

        var approverSvc = SensitiveActionsTestHelpers.NewService(
            db, SensitiveActionsTestHelpers.NewCaller(Approver),
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() },
            handlers: new[] { (Cnas.Ps.Application.SensitiveActions.ISensitiveActionHandler)new SensitiveActionsTestHelpers.FakeUserStateChangeHandler() });
        var result = await approverSvc.ApproveAsync(
            created.Value.Id,
            new SensitiveAdminActionApprovalInputDto("Approving — please proceed."),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var row = await db.SensitiveAdminActions.SingleAsync();
        row.Status.Should().Be(SensitiveAdminActionStatus.Executed);
        row.ExecutionResultJson.Should().Be("{\"executed\":true}");
        row.ApprovedByUserId.Should().Be(Approver);
    }

    [Fact]
    public async Task Reject_SameOperator_ReturnsFourEyesConflict()
    {
        using var db = SensitiveActionsTestHelpers.CreateContext();
        var caller = SensitiveActionsTestHelpers.NewCaller(Requester);
        var svc = SensitiveActionsTestHelpers.NewService(db, caller,
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() });
        var created = await svc.RequestAsync(ValidRequest(), CancellationToken.None);

        var result = await svc.RejectAsync(
            created.Value.Id,
            new SensitiveAdminActionReasonInputDto("Rejecting my own request to test the guard."),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.FourEyesSameOperator);
    }

    [Fact]
    public async Task Cancel_AfterApproved_ReturnsAlreadyDecided()
    {
        using var db = SensitiveActionsTestHelpers.CreateContext();
        var requesterSvc = SensitiveActionsTestHelpers.NewService(
            db, SensitiveActionsTestHelpers.NewCaller(Requester),
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() },
            handlers: new[] { (Cnas.Ps.Application.SensitiveActions.ISensitiveActionHandler)new SensitiveActionsTestHelpers.FakeUserStateChangeHandler() });
        var created = await requesterSvc.RequestAsync(ValidRequest(), CancellationToken.None);

        var approverSvc = SensitiveActionsTestHelpers.NewService(
            db, SensitiveActionsTestHelpers.NewCaller(Approver),
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() },
            handlers: new[] { (Cnas.Ps.Application.SensitiveActions.ISensitiveActionHandler)new SensitiveActionsTestHelpers.FakeUserStateChangeHandler() });
        await approverSvc.ApproveAsync(
            created.Value.Id,
            new SensitiveAdminActionApprovalInputDto("Approving for the test."),
            CancellationToken.None);

        // Requester tries to cancel after the row has been approved + executed.
        var cancelResult = await requesterSvc.CancelAsync(
            created.Value.Id,
            new SensitiveAdminActionReasonInputDto("Changed my mind — please cancel."),
            CancellationToken.None);

        cancelResult.IsFailure.Should().BeTrue();
        cancelResult.ErrorCode.Should().Be(ErrorCodes.FourEyesAlreadyDecided);
    }

    [Fact]
    public async Task SweepExpired_FlipsOneRowAndReturnsCount()
    {
        using var db = SensitiveActionsTestHelpers.CreateContext();
        var caller = SensitiveActionsTestHelpers.NewCaller(Requester);
        var svc = SensitiveActionsTestHelpers.NewService(db, caller,
            policies: new[] { new SensitiveActionsTestHelpers.FakeUserStateChangePolicy() });

        // Seed one expired-pending row directly so the test does not depend on the
        // request path's clock manipulation.
        db.SensitiveAdminActions.Add(new SensitiveAdminAction
        {
            ActionCode = FakeActionCode,
            Status = SensitiveAdminActionStatus.PendingApproval,
            RequestedByUserId = Requester,
            RequestedAt = SensitiveActionsTestHelpers.ClockNow.AddDays(-5),
            RequestReason = "Old request",
            RequestPayloadJson = "{}",
            ExpiresAt = SensitiveActionsTestHelpers.ClockNow.AddDays(-2),
            CreatedAtUtc = SensitiveActionsTestHelpers.ClockNow.AddDays(-5),
            IsActive = true,
        });
        // And one fresh row that should NOT be touched.
        db.SensitiveAdminActions.Add(new SensitiveAdminAction
        {
            ActionCode = FakeActionCode,
            Status = SensitiveAdminActionStatus.PendingApproval,
            RequestedByUserId = Requester,
            RequestedAt = SensitiveActionsTestHelpers.ClockNow,
            RequestReason = "Fresh request",
            RequestPayloadJson = "{}",
            ExpiresAt = SensitiveActionsTestHelpers.ClockNow.AddDays(1),
            CreatedAtUtc = SensitiveActionsTestHelpers.ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await svc.SweepExpiredAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        var expired = await db.SensitiveAdminActions
            .Where(r => r.Status == SensitiveAdminActionStatus.Expired)
            .ToListAsync();
        expired.Should().HaveCount(1);
    }

    /// <summary>
    /// Inline policy used by the "default expiration window" test. Accepts any payload
    /// and exposes a null override so the substrate falls back to its default 72h
    /// window.
    /// </summary>
    /// <param name="actionCode">The action code this policy claims.</param>
    private sealed class InlineFakePolicy(string actionCode)
        : Cnas.Ps.Application.SensitiveActions.ISensitiveActionPolicy
    {
        /// <inheritdoc />
        public string ActionCode { get; } = actionCode;

        /// <inheritdoc />
        public string DisplayLabel => "Inline test policy";

        /// <inheritdoc />
        public TimeSpan? ExpirationOverride => null;

        /// <inheritdoc />
        public Task<Result> ValidatePayloadAsync(string payloadJson, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }
}
