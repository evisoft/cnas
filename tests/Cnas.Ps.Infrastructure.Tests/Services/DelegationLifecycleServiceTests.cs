using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0057 / TOR SEC 026 + CF 16.11 — service-layer tests for
/// <see cref="DelegationLifecycleService"/>. Uses EF Core InMemory + NSubstitute,
/// mirroring the harness pattern from <see cref="PendingAdminActionServiceTests"/>.
/// Each test isolates one branch of the grant / revoke / list flow.
/// </summary>
public sealed class DelegationLifecycleServiceTests
{
    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Default scope used across happy-path tests.</summary>
    private const string DefaultScope = "approve.executory_documents";

    // ─────────────────────── GrantAsync ───────────────────────

    [Fact]
    public async Task GrantAsync_HappyPath_PersistsRow_AndAuditsCritical()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.GrantAsync(
            delegateeSqid: harness.DelegateeSqid,
            validFromUtc: ClockNow,
            validToUtc: ClockNow.AddDays(30),
            suspendsGrantorRights: true,
            scope: DefaultScope);

        result.IsSuccess.Should().BeTrue();
        // Returned DTO carries Sqid-encoded ids.
        result.Value.Id.Should().StartWith("SQID-");
        result.Value.GrantorUserId.Should().Be(harness.GrantorSqid);
        result.Value.DelegateeUserId.Should().Be(harness.DelegateeSqid);
        result.Value.Scope.Should().Be(DefaultScope);
        result.Value.SuspendsGrantorRights.Should().BeTrue();
        result.Value.GrantedAtUtc.Should().Be(ClockNow);
        result.Value.RevokedAtUtc.Should().BeNull();

        // One row persisted with the expected shape.
        var row = await harness.Db.DelegationGrants.SingleAsync();
        row.GrantorUserId.Should().Be(Harness.GrantorUserId);
        row.DelegateeUserId.Should().Be(Harness.DelegateeUserId);
        row.Scope.Should().Be(DefaultScope);
        row.SuspendsGrantorRights.Should().BeTrue();
        row.RevokedAtUtc.Should().BeNull();

        // Audit row: stable event code, Critical severity, target id is the grant pk.
        await harness.Audit.Received(1).RecordAsync(
            "DELEGATION.GRANTED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(DelegationGrant),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GrantAsync_WindowExceedsCap_ReturnsValidationFailed()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.GrantAsync(
            delegateeSqid: harness.DelegateeSqid,
            validFromUtc: ClockNow,
            // 91 days > 90-day operational cap.
            validToUtc: ClockNow.AddDays(91),
            suspendsGrantorRights: false,
            scope: DefaultScope);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await harness.Db.DelegationGrants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GrantAsync_InvertedWindow_ReturnsValidationFailed()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.GrantAsync(
            delegateeSqid: harness.DelegateeSqid,
            validFromUtc: ClockNow,
            validToUtc: ClockNow.AddDays(-1),
            suspendsGrantorRights: false,
            scope: DefaultScope);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task GrantAsync_SelfDelegation_ReturnsValidationFailed()
    {
        var harness = await Harness.CreateAsync();

        // Calling user (grantor) attempts to grant to themselves.
        var result = await harness.Service.GrantAsync(
            delegateeSqid: harness.GrantorSqid,
            validFromUtc: ClockNow,
            validToUtc: ClockNow.AddDays(10),
            suspendsGrantorRights: false,
            scope: DefaultScope);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await harness.Db.DelegationGrants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GrantAsync_UnknownDelegatee_ReturnsNotFound()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.GrantAsync(
            delegateeSqid: "SQID-9999",
            validFromUtc: ClockNow,
            validToUtc: ClockNow.AddDays(10),
            suspendsGrantorRights: false,
            scope: DefaultScope);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── RevokeAsync ───────────────────────

    [Fact]
    public async Task RevokeAsync_HappyPath_StampsRevocation_AndAuditsCritical()
    {
        var harness = await Harness.CreateAsync();
        var grant = await harness.Service.GrantAsync(
            harness.DelegateeSqid,
            ClockNow,
            ClockNow.AddDays(30),
            suspendsGrantorRights: false,
            scope: DefaultScope);

        var result = await harness.Service.RevokeAsync(grant.Value.Id, "Project closed early.");

        result.IsSuccess.Should().BeTrue();
        var row = await harness.Db.DelegationGrants.SingleAsync();
        row.RevokedAtUtc.Should().Be(ClockNow);
        row.RevokeReason.Should().Be("Project closed early.");

        await harness.Audit.Received(1).RecordAsync(
            "DELEGATION.REVOKED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(DelegationGrant),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeAsync_UnknownGrant_ReturnsNotFound()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.RevokeAsync("SQID-9999", "Reason.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task RevokeAsync_NotGrantor_ReturnsForbidden()
    {
        var harness = await Harness.CreateAsync();
        var grant = await harness.Service.GrantAsync(
            harness.DelegateeSqid,
            ClockNow,
            ClockNow.AddDays(30),
            suspendsGrantorRights: false,
            scope: DefaultScope);

        // Switch caller to an unrelated user — neither grantor nor delegatee.
        var stranger = harness.WithCaller(Harness.StrangerUserId, "SQID-STRANGER");

        var result = await stranger.Service.RevokeAsync(grant.Value.Id, "Reason.");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    // ─────────────────────── ListActiveAsync ───────────────────────

    [Fact]
    public async Task ListActiveAsync_FiltersByGrantor_AndExcludesRevoked()
    {
        var harness = await Harness.CreateAsync();

        // Three grants by the same grantor; one will be revoked.
        var first = await harness.Service.GrantAsync(
            harness.DelegateeSqid, ClockNow, ClockNow.AddDays(10), false, DefaultScope);
        var second = await harness.Service.GrantAsync(
            harness.DelegateeSqid, ClockNow, ClockNow.AddDays(20), false, "approve.bass");
        var third = await harness.Service.GrantAsync(
            harness.DelegateeSqid, ClockNow, ClockNow.AddDays(30), false, "approve.recovery");
        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        third.IsSuccess.Should().BeTrue();

        // Revoke one of them — it must drop from ListActive.
        (await harness.Service.RevokeAsync(second.Value.Id, "Project paused.")).IsSuccess.Should().BeTrue();

        var result = await harness.Service.ListActiveAsync(harness.GrantorSqid);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Should().NotContain(g => g.Id == second.Value.Id);
        // Ordered by ValidFromUtc ascending.
        result.Value.Should().BeInAscendingOrder(g => g.ValidFromUtc);
    }

    [Fact]
    public async Task ListActiveAsync_UnknownUser_ReturnsNotFound()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.ListActiveAsync("SQID-9999");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-delegation-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public const long GrantorUserId = 1001L;
        public const long DelegateeUserId = 1002L;
        public const long StrangerUserId = 1003L;

        public required CnasDbContext Db { get; init; }
        public required DelegationLifecycleService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required ISqidService Sqids { get; init; }
        public string GrantorSqid => $"SQID-{GrantorUserId}";
        public string DelegateeSqid => $"SQID-{DelegateeUserId}";

        public static async Task<Harness> CreateAsync()
        {
            var db = CreateContext();
            await SeedUsersAsync(db);
            return BuildAround(db, GrantorUserId, $"SQID-{GrantorUserId}", new StubClock(ClockNow));
        }

        public Harness WithCaller(long userId, string userSqid) =>
            BuildAround(Db, userId, userSqid, new StubClock(ClockNow), Sqids, Audit);

        private static async Task SeedUsersAsync(CnasDbContext db)
        {
            db.UserProfiles.AddRange(
                new UserProfile
                {
                    Id = GrantorUserId,
                    MPassSubject = "sub-grantor",
                    DisplayName = "Grantor",
                    Email = "grantor@example.md",
                    Roles = ["cnas-user"],
                    CreatedAtUtc = ClockNow.AddDays(-1),
                    IsActive = true,
                    State = UserAccountState.Active,
                },
                new UserProfile
                {
                    Id = DelegateeUserId,
                    MPassSubject = "sub-delegatee",
                    DisplayName = "Delegatee",
                    Email = "delegatee@example.md",
                    Roles = ["cnas-user"],
                    CreatedAtUtc = ClockNow.AddDays(-1),
                    IsActive = true,
                    State = UserAccountState.Active,
                },
                new UserProfile
                {
                    Id = StrangerUserId,
                    MPassSubject = "sub-stranger",
                    DisplayName = "Stranger",
                    Email = "stranger@example.md",
                    Roles = ["cnas-user"],
                    CreatedAtUtc = ClockNow.AddDays(-1),
                    IsActive = true,
                    State = UserAccountState.Active,
                });
            await db.SaveChangesAsync();
        }

        private static Harness BuildAround(
            CnasDbContext db,
            long callerUserId,
            string callerSqid,
            ICnasTimeProvider clock,
            ISqidService? sharedSqids = null,
            IAuditService? sharedAudit = null)
        {
            var sqids = sharedSqids ?? Substitute.For<ISqidService>();
            if (sharedSqids is null)
            {
                sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
                sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
                {
                    var arg = call.Arg<string?>();
                    if (!string.IsNullOrEmpty(arg)
                        && arg.StartsWith("SQID-", StringComparison.Ordinal)
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
            caller.Roles.Returns(["cnas-user"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns($"corr-{callerUserId}");

            var audit = sharedAudit ?? Substitute.For<IAuditService>();
            if (sharedAudit is null)
            {
                audit.RecordAsync(
                        Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                        Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Result.Success());
            }

            var service = new DelegationLifecycleService(db, sqids, clock, caller, audit);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Sqids = sqids,
            };
        }
    }
}
