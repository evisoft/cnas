using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — service-layer tests for
/// <see cref="InsolvencyLifecycleService"/>. Uses EF Core InMemory + NSubstitute,
/// mirroring the harness pattern from <see cref="DelegationLifecycleServiceTests"/>.
/// </summary>
public sealed class InsolvencyLifecycleServiceTests
{
    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Today's date corresponding to the clock anchor.</summary>
    private static readonly DateOnly Today = DateOnly.FromDateTime(ClockNow);

    /// <summary>Default reason used across happy-path tests.</summary>
    private const string DefaultReason = "Hotărâre judecătorească nr. 1234/2026";

    [Fact]
    public async Task OpenAsync_HappyPath_FlipsFlag_PersistsRow_AndAuditsCritical()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.OpenAsync(
            harness.ContributorSqid,
            DefaultReason,
            Today);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().StartWith("SQID-");
        result.Value.ContributorSqid.Should().Be(harness.ContributorSqid);
        result.Value.Status.Should().Be("Open");
        result.Value.Reason.Should().Be(DefaultReason);
        result.Value.OpenedAtUtc.Should().Be(ClockNow);
        result.Value.ResolvedAtUtc.Should().BeNull();

        // Row persisted and flag flipped on the parent contributor.
        var row = await harness.Db.InsolvencyCases.SingleAsync();
        row.Status.Should().Be(InsolvencyCaseStatus.Open);
        row.Reason.Should().Be(DefaultReason);
        var contributor = await harness.Db.Contributors.SingleAsync();
        contributor.IsInsolvent.Should().BeTrue();

        await harness.Audit.Received(1).RecordAsync(
            "INSOLVENCY.OPENED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(InsolvencyCase),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenAsync_FutureDate_ReturnsValidationFailed()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.OpenAsync(
            harness.ContributorSqid,
            DefaultReason,
            Today.AddDays(1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await harness.Db.InsolvencyCases.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OpenAsync_UnknownContributor_ReturnsNotFound()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.OpenAsync(
            "SQID-9999",
            DefaultReason,
            Today);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task OpenAsync_AlreadyOpen_ReturnsConflict()
    {
        var harness = await Harness.CreateAsync();
        var first = await harness.Service.OpenAsync(
            harness.ContributorSqid, DefaultReason, Today);
        first.IsSuccess.Should().BeTrue();

        var second = await harness.Service.OpenAsync(
            harness.ContributorSqid, "Second attempt", Today);

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
        (await harness.Db.InsolvencyCases.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_HappyPath_StampsResolution_AndUnflips_AndAuditsCritical()
    {
        var harness = await Harness.CreateAsync();
        var open = await harness.Service.OpenAsync(
            harness.ContributorSqid, DefaultReason, Today);
        open.IsSuccess.Should().BeTrue();

        var result = await harness.Service.ResolveAsync(
            open.Value.Id, "Plătit integral pe 2026-12-01");

        result.IsSuccess.Should().BeTrue();
        var row = await harness.Db.InsolvencyCases.SingleAsync();
        row.Status.Should().Be(InsolvencyCaseStatus.Resolved);
        row.ResolvedAtUtc.Should().Be(ClockNow);
        row.Resolution.Should().Be("Plătit integral pe 2026-12-01");
        var contributor = await harness.Db.Contributors.SingleAsync();
        contributor.IsInsolvent.Should().BeFalse();

        await harness.Audit.Received(1).RecordAsync(
            "INSOLVENCY.RESOLVED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(InsolvencyCase),
            row.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_AlreadyResolved_ReturnsConflict()
    {
        var harness = await Harness.CreateAsync();
        var open = await harness.Service.OpenAsync(
            harness.ContributorSqid, DefaultReason, Today);
        (await harness.Service.ResolveAsync(open.Value.Id, "First resolution"))
            .IsSuccess.Should().BeTrue();

        var second = await harness.Service.ResolveAsync(open.Value.Id, "Second resolution");

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>
    /// iter-149 — Fix 7: a second OpenAsync against the same contributor (after
    /// the first opened a case) returns Conflict. Verifies the pre-check guard
    /// (the partial unique index is not enforced under InMemory; the catch path
    /// is exercised in production against Postgres).
    /// </summary>
    [Fact]
    public async Task OpenAsync_DoubleOpen_ReturnsConflict()
    {
        var harness = await Harness.CreateAsync();
        var first = await harness.Service.OpenAsync(
            harness.ContributorSqid, DefaultReason, Today);
        first.IsSuccess.Should().BeTrue();

        var second = await harness.Service.OpenAsync(
            harness.ContributorSqid, "Second concurrent open", Today);

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
        (await harness.Db.InsolvencyCases.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// iter-149 — Fix 8: ResolveAsync refuses when the contributor row is
    /// missing (was soft-deleted independently). Previously the resolution
    /// landed without flipping the legacy bit-flag, leaving the system in a
    /// split-brain state.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_MissingContributor_ReturnsConflict()
    {
        var harness = await Harness.CreateAsync();
        var open = await harness.Service.OpenAsync(
            harness.ContributorSqid, DefaultReason, Today);
        open.IsSuccess.Should().BeTrue();
        // Soft-delete the contributor independently — the case row remains.
        var contributor = await harness.Db.Contributors.SingleAsync();
        contributor.IsActive = false;
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.ResolveAsync(open.Value.Id, "Plătit integral");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
        // Case row must NOT have been flipped to Resolved.
        var row = await harness.Db.InsolvencyCases.SingleAsync();
        row.Status.Should().Be(InsolvencyCaseStatus.Open);
        row.ResolvedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task ListActiveAsync_ReturnsOnlyOpenRows_OrderedByOpenedAtAscending()
    {
        var harness = await Harness.CreateAsync();
        // Seed a second contributor so we can open two cases concurrently.
        await harness.SeedSecondContributorAsync();

        var first = await harness.Service.OpenAsync(
            harness.ContributorSqid, "First", Today);
        var second = await harness.Service.OpenAsync(
            harness.SecondContributorSqid, "Second", Today);
        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();

        // Resolve one — it should drop from ListActive.
        (await harness.Service.ResolveAsync(second.Value.Id, "Closed out")).IsSuccess.Should().BeTrue();

        var result = await harness.Service.ListActiveAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value.Single().Id.Should().Be(first.Value.Id);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-insolvency-{Guid.NewGuid():N}")
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
        public const long ContributorId = 4001L;
        public const long SecondContributorId = 4002L;

        public required CnasDbContext Db { get; init; }
        public required InsolvencyLifecycleService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public string ContributorSqid => $"SQID-{ContributorId}";
        public string SecondContributorSqid => $"SQID-{SecondContributorId}";

        public static async Task<Harness> CreateAsync()
        {
            var db = CreateContext();
            db.Contributors.Add(new Contributor
            {
                Id = ContributorId,
                Idno = "2000000000007",
                IdnoHash = "hash-1",
                Denumire = "Acme SRL",
                RegisteredAtUtc = ClockNow.AddYears(-1),
                IsActive = true,
                CreatedAtUtc = ClockNow.AddYears(-1),
            });
            await db.SaveChangesAsync();

            return Build(db, new StubClock(ClockNow));
        }

        public async Task SeedSecondContributorAsync()
        {
            Db.Contributors.Add(new Contributor
            {
                Id = SecondContributorId,
                Idno = "1003600012346",
                IdnoHash = "hash-2",
                Denumire = "Beta SRL",
                RegisteredAtUtc = ClockNow.AddYears(-1),
                IsActive = true,
                CreatedAtUtc = ClockNow.AddYears(-1),
            });
            await Db.SaveChangesAsync();
        }

        private static Harness Build(CnasDbContext db, ICnasTimeProvider clock)
        {
            var sqids = Substitute.For<ISqidService>();
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

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(101L);
            caller.UserSqid.Returns("SQID-101");
            caller.Roles.Returns(["cnas-admin"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-test");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Result.Success());

            var service = new InsolvencyLifecycleService(db, sqids, clock, caller, audit);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
            };
        }
    }
}
