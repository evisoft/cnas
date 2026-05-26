using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.AccessScope;
using Cnas.Ps.Application.SensitiveActions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.SensitiveActions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.SensitiveActions;

/// <summary>
/// R2273 / TOR SEC 027 — shared helpers for the sensitive-admin-action test suite. The
/// helpers spin up an in-memory DbContext, a stub clock, a stub caller, a Sqid mock, a
/// no-op audit substitute, and the full validator set — collapsing the boilerplate per
/// test into a single constructor call.
/// </summary>
internal static class SensitiveActionsTestHelpers
{
    /// <summary>Canonical "now" used across the suite — keeps timestamp asserts deterministic.</summary>
    public static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Creates a fresh InMemory-backed <see cref="CnasDbContext"/>.</summary>
    /// <returns>A new context backed by a uniquely-named store.</returns>
    public static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-sens-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Builds an <see cref="ISqidService"/> mock whose <c>Encode</c> formats as
    /// <c>SQID-{id}</c> and whose <c>TryDecode</c> reverses that mapping.
    /// </summary>
    /// <returns>The Sqid substitute.</returns>
    public static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var s = call.Arg<string?>();
            if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s["SQID-".Length..], out var v))
            {
                return Result<long>.Success(v);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "n/a");
        });
        return sqids;
    }

    /// <summary>Shared role array stamped onto every caller mock — keeps CA1861 happy.</summary>
    private static readonly string[] AdminRoles = new[] { "cnas-admin" };

    /// <summary>Builds an authenticated caller substitute identified by <paramref name="userId"/>.</summary>
    /// <param name="userId">Authenticated user id stamped on the caller context.</param>
    /// <returns>The caller substitute.</returns>
    public static ICallerContext NewCaller(long userId)
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns((long?)userId);
        caller.UserSqid.Returns($"SQID-{userId}");
        caller.Roles.Returns(AdminRoles);
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-test");
        caller.AccessScope.Returns(Substitute.For<IAccessScope>());
        return caller;
    }

    /// <summary>Builds a no-op audit substitute that returns success on every call.</summary>
    /// <returns>The audit substitute.</returns>
    public static IAuditService NewAudit()
    {
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        return audit;
    }

    /// <summary>Constructs a fully-wired service for a single test case.</summary>
    /// <param name="db">The shared DB context.</param>
    /// <param name="caller">Caller context for the call under test.</param>
    /// <param name="audit">Audit substitute (default: no-op).</param>
    /// <param name="policies">Registered policies (default: empty).</param>
    /// <param name="handlers">Registered handlers (default: empty).</param>
    /// <returns>The service.</returns>
    public static SensitiveAdminActionService NewService(
        CnasDbContext db,
        ICallerContext caller,
        IAuditService? audit = null,
        IEnumerable<ISensitiveActionPolicy>? policies = null,
        IEnumerable<ISensitiveActionHandler>? handlers = null)
    {
        var p = (policies ?? Array.Empty<ISensitiveActionPolicy>()).ToList();
        var h = (handlers ?? Array.Empty<ISensitiveActionHandler>()).ToList();
        var registry = new SensitiveActionRegistry(p);
        return new SensitiveAdminActionService(
            db: db,
            sqids: NewSqidMock(),
            clock: new StubClock(ClockNow),
            caller: caller,
            audit: audit ?? NewAudit(),
            registry: registry,
            policies: p,
            handlers: h,
            requestValidator: new SensitiveAdminActionRequestInputValidator(),
            approvalValidator: new SensitiveAdminActionApprovalInputValidator(),
            reasonValidator: new SensitiveAdminActionReasonInputValidator(),
            filterValidator: new SensitiveAdminActionFilterValidator());
    }

    /// <summary>Test-only clock returning a fixed UTC instant.</summary>
    public sealed class StubClock : ICnasTimeProvider
    {
        /// <summary>Constructs the clock at the supplied instant.</summary>
        /// <param name="now">Instant returned from <see cref="UtcNow"/>.</param>
        public StubClock(DateTime now) { UtcNow = now; }

        /// <inheritdoc />
        public DateTime UtcNow { get; }
    }

    /// <summary>
    /// Test-only policy + handler pair that ALWAYS accepts the payload and emits a
    /// stable canned result string. Used by the "registered handler executes" test.
    /// </summary>
    public sealed class FakeUserStateChangePolicy : ISensitiveActionPolicy
    {
        /// <summary>Stable action code this fake claims.</summary>
        public const string Code = "USER.ACCOUNT_STATE_CHANGE";

        /// <inheritdoc />
        public string ActionCode => Code;

        /// <inheritdoc />
        public string DisplayLabel => "Change user account state";

        /// <inheritdoc />
        public TimeSpan? ExpirationOverride => TimeSpan.FromHours(48);

        /// <inheritdoc />
        public Task<Result> ValidatePayloadAsync(string payloadJson, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    /// <summary>Test-only handler that records the action id and returns a canned JSON result.</summary>
    public sealed class FakeUserStateChangeHandler : ISensitiveActionHandler
    {
        /// <inheritdoc />
        public string ActionCode => FakeUserStateChangePolicy.Code;

        /// <inheritdoc />
        public Task<Result<string?>> ExecuteAsync(SensitiveAdminAction action, CancellationToken ct = default)
            => Task.FromResult(Result<string?>.Success("{\"executed\":true}"));
    }
}
