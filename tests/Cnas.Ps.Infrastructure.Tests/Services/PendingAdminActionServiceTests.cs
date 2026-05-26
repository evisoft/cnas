using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Service-layer tests for <see cref="PendingAdminActionService"/> (R0058 / SEC 027).
/// Uses EF Core InMemory + NSubstitute, mirroring the pattern in
/// <see cref="UserAdministrationServiceTests"/>. Each test exercises one branch of the
/// 4-eyes / maker-checker workflow: submit / approve / reject / list / TTL-expiry.
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — Submit / Approve / Reject / TTL paths
/// all emit on the static <see cref="Cnas.Ps.Infrastructure.Observability.CnasMeter"/>.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class PendingAdminActionServiceTests
{
    /// <summary>Deterministic clock anchor for all tests.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Stable operation code used by the test executor.</summary>
    private const string DemoOperation = "DEMO.NOOP";

    /// <summary>Operation code that no registered executor handles.</summary>
    private const string UnknownOperation = "DEMO.UNKNOWN";

    // ─────────────────────── SubmitAsync ───────────────────────

    [Fact]
    public async Task SubmitAsync_UnknownOperation_ReturnsUnknownOperation()
    {
        var harness = Harness.Create();

        var result = await harness.Service.SubmitAsync(UnknownOperation, "{}");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MakerCheckerUnknownOperation);
        // Nothing was persisted because the executor was rejected fail-fast at submit time.
        (await harness.Db.PendingAdminActions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_KnownOperation_PersistsPendingRow_AndReturnsSqid()
    {
        var harness = Harness.Create();

        var result = await harness.Service.SubmitAsync(DemoOperation, "{\"x\":1}");

        result.IsSuccess.Should().BeTrue();
        // The returned id is the Sqid form of the new row's primary key.
        result.Value.Should().StartWith("SQID-");

        var row = await harness.Db.PendingAdminActions.SingleAsync();
        row.Operation.Should().Be(DemoOperation);
        row.PayloadJson.Should().Be("{\"x\":1}");
        row.Status.Should().Be(PendingAdminActionStatus.Pending);
        row.MakerUserId.Should().Be(Harness.MakerUserId);
        row.MakerRequestedAtUtc.Should().Be(ClockNow);
        row.CheckerUserId.Should().BeNull();
        row.CheckerDecidedAtUtc.Should().BeNull();
        // Default TTL is 24h (the spec); ExpiresAtUtc = MakerRequestedAtUtc + 24h.
        row.ExpiresAtUtc.Should().Be(ClockNow.AddHours(24));
    }

    // ─────────────────────── ApproveAsync ───────────────────────

    [Fact]
    public async Task ApproveAsync_BySameUserAsMaker_ReturnsSelfApprovalForbidden()
    {
        var harness = Harness.Create();
        // Submit as the maker (caller = MakerUserId).
        var submit = await harness.Service.SubmitAsync(DemoOperation, "{}");
        submit.IsSuccess.Should().BeTrue();

        // Approve called by the SAME caller — must be rejected.
        var approve = await harness.Service.ApproveAsync(submit.Value);

        approve.IsFailure.Should().BeTrue();
        approve.ErrorCode.Should().Be(ErrorCodes.MakerCheckerSelfApprovalForbidden);
        // The row stayed Pending; the executor was NOT invoked.
        var row = await harness.Db.PendingAdminActions.SingleAsync();
        row.Status.Should().Be(PendingAdminActionStatus.Pending);
        harness.Executor.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task ApproveAsync_AfterTtl_FlipsStatusToExpired_AndReturnsExpired()
    {
        var harness = Harness.Create();
        var submit = await harness.Service.SubmitAsync(DemoOperation, "{}");

        // Advance the clock past the TTL window (default 24h) and rebuild the service so
        // it sees the new "now".
        var laterClock = new StubClock(ClockNow.AddHours(25));
        var laterChecker = harness.WithCaller(Harness.CheckerUserId, "SQID-CHECKER", laterClock);

        var approve = await laterChecker.Service.ApproveAsync(submit.Value);

        approve.IsFailure.Should().BeTrue();
        approve.ErrorCode.Should().Be(ErrorCodes.MakerCheckerExpired);
        var row = await harness.Db.PendingAdminActions.SingleAsync();
        row.Status.Should().Be(PendingAdminActionStatus.Expired);
        harness.Executor.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task ApproveAsync_ByDifferentUser_FlipsStatusToApproved_AndInvokesExecutor()
    {
        var harness = Harness.Create();
        var submit = await harness.Service.SubmitAsync(DemoOperation, "{\"id\":42}");

        // Switch caller to a different admin acting as checker.
        var checkerHarness = harness.WithCaller(Harness.CheckerUserId, "SQID-CHECKER");

        var approve = await checkerHarness.Service.ApproveAsync(submit.Value);

        approve.IsSuccess.Should().BeTrue();
        var row = await harness.Db.PendingAdminActions.SingleAsync();
        row.Status.Should().Be(PendingAdminActionStatus.Approved);
        row.CheckerUserId.Should().Be(Harness.CheckerUserId);
        row.CheckerDecidedAtUtc.Should().Be(ClockNow);
        // Executor saw the original payload verbatim.
        harness.Executor.Invocations.Should().Be(1);
        harness.Executor.LastOperation.Should().Be(DemoOperation);
        harness.Executor.LastPayload.Should().Be("{\"id\":42}");
    }

    [Fact]
    public async Task ApproveAsync_AlreadyApproved_ReturnsAlreadyDecided()
    {
        var harness = Harness.Create();
        var submit = await harness.Service.SubmitAsync(DemoOperation, "{}");
        var checkerHarness = harness.WithCaller(Harness.CheckerUserId, "SQID-CHECKER");
        // First approval succeeds.
        (await checkerHarness.Service.ApproveAsync(submit.Value)).IsSuccess.Should().BeTrue();

        // Second approval (idempotent guard) must report already-decided.
        var second = await checkerHarness.Service.ApproveAsync(submit.Value);

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.MakerCheckerAlreadyDecided);
        // Executor still only invoked once — duplicate execution prevented.
        harness.Executor.Invocations.Should().Be(1);
    }

    // ─────────────────────── RejectAsync ───────────────────────

    [Fact]
    public async Task RejectAsync_ByDifferentUser_FlipsStatusToRejected_AndDoesNotInvokeExecutor()
    {
        var harness = Harness.Create();
        var submit = await harness.Service.SubmitAsync(DemoOperation, "{}");
        var checkerHarness = harness.WithCaller(Harness.CheckerUserId, "SQID-CHECKER");

        var reject = await checkerHarness.Service.RejectAsync(submit.Value, "Insufficient justification.");

        reject.IsSuccess.Should().BeTrue();
        var row = await harness.Db.PendingAdminActions.SingleAsync();
        row.Status.Should().Be(PendingAdminActionStatus.Rejected);
        row.CheckerUserId.Should().Be(Harness.CheckerUserId);
        row.CheckerDecidedAtUtc.Should().Be(ClockNow);
        row.RejectionReason.Should().Be("Insufficient justification.");
        harness.Executor.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task RejectAsync_BySameUserAsMaker_ReturnsSelfApprovalForbidden()
    {
        var harness = Harness.Create();
        var submit = await harness.Service.SubmitAsync(DemoOperation, "{}");

        // Reject called by the SAME caller as maker — 4-eyes requires another admin.
        var reject = await harness.Service.RejectAsync(submit.Value, "Reason.");

        reject.IsFailure.Should().BeTrue();
        reject.ErrorCode.Should().Be(ErrorCodes.MakerCheckerSelfApprovalForbidden);
        var row = await harness.Db.PendingAdminActions.SingleAsync();
        row.Status.Should().Be(PendingAdminActionStatus.Pending);
    }

    // ─────────────────────── ListPendingAsync ───────────────────────

    [Fact]
    public async Task ListPendingAsync_FiltersExpired_AndPaginates()
    {
        var harness = Harness.Create();
        // Submit three actions, then expire one of them manually.
        var first = await harness.Service.SubmitAsync(DemoOperation, "{\"k\":1}");
        var second = await harness.Service.SubmitAsync(DemoOperation, "{\"k\":2}");
        var third = await harness.Service.SubmitAsync(DemoOperation, "{\"k\":3}");
        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        third.IsSuccess.Should().BeTrue();

        // Manually expire one row by setting ExpiresAtUtc into the past.
        var expired = await harness.Db.PendingAdminActions.OrderBy(p => p.Id).FirstAsync();
        expired.ExpiresAtUtc = ClockNow.AddDays(-1);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.ListPendingAsync(new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        // Expired row is filtered out.
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Should().HaveCount(2);
        result.Value.Items.Should().AllSatisfy(item =>
        {
            item.Id.Should().StartWith("SQID-");
            item.Operation.Should().Be(DemoOperation);
            // Maker user is exposed as Sqid only, not raw id or email/IDNP.
            item.MakerUserId.Should().StartWith("SQID-");
        });
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-pendingadm-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Test executor that records every invocation for assertion.</summary>
    private sealed class RecordingExecutor : IPendingAdminActionExecutor
    {
        public int Invocations { get; private set; }
        public string? LastOperation { get; private set; }
        public string? LastPayload { get; private set; }

        public bool Handles(string operation) =>
            string.Equals(operation, DemoOperation, StringComparison.Ordinal);

        public Task<Result> ExecuteAsync(string operation, string payloadJson, CancellationToken ct = default)
        {
            Invocations++;
            LastOperation = operation;
            LastPayload = payloadJson;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class Harness
    {
        /// <summary>UserId of the maker (default submitter).</summary>
        public const long MakerUserId = 1001L;

        /// <summary>UserId of the checker (different admin who approves/rejects).</summary>
        public const long CheckerUserId = 1002L;

        public required CnasDbContext Db { get; init; }
        public required PendingAdminActionService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required RecordingExecutor Executor { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            return BuildAround(db, MakerUserId, "SQID-MAKER", new StubClock(ClockNow));
        }

        /// <summary>Builds a sibling harness that shares the same DbContext but a different caller / clock.</summary>
        public Harness WithCaller(long userId, string userSqid, StubClock? clock = null) =>
            BuildAround(Db, userId, userSqid, clock ?? new StubClock(ClockNow), Sqids, Executor);

        private static Harness BuildAround(
            CnasDbContext db,
            long callerUserId,
            string callerSqid,
            ICnasTimeProvider clock,
            ISqidService? sharedSqids = null,
            RecordingExecutor? sharedExecutor = null)
        {
            var sqids = sharedSqids ?? Substitute.For<ISqidService>();
            if (sharedSqids is null)
            {
                sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
                // TryDecode is configured per id below — but install a default that decodes
                // any "SQID-{n}" string to its numeric tail so the service can resolve the
                // submitted row by Sqid in approve/reject paths.
                sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
                {
                    var arg = call.Arg<string?>();
                    if (!string.IsNullOrEmpty(arg) && arg.StartsWith("SQID-", StringComparison.Ordinal)
                        && long.TryParse(arg.AsSpan(5), out var n))
                    {
                        return Result<long>.Success(n);
                    }
                    return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
                });
            }

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(callerUserId);
            caller.UserSqid.Returns(callerSqid);
            caller.Roles.Returns(["cnas-admin"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns($"corr-{callerUserId}");

            var executor = sharedExecutor ?? new RecordingExecutor();
            IEnumerable<IPendingAdminActionExecutor> executors = [executor];

            var service = new PendingAdminActionService(db, sqids, clock, caller, executors);
            return new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
                Executor = executor,
            };
        }
    }
}
