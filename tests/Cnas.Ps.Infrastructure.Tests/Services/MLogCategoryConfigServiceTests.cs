using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.MLog;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0116 + R0195 — pins the contract of <see cref="MLogCategoryConfigService"/>.
/// </summary>
public sealed class MLogCategoryConfigServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-mlog-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class StubCache : IMLogCategoryFilterCache
    {
        public int InvalidateCount { get; private set; }
        public void Invalidate() => InvalidateCount++;
    }

    private sealed record Harness(
        CnasDbContext Db,
        MLogCategoryConfigService Sut,
        IAuditService Audit,
        ISqidService Sqids,
        StubCache Cache);

    private static Harness Create()
    {
        var db = CreateContext();
        var roDb = Substitute.For<IReadOnlyCnasDbContext>();
        roDb.MLogCategoryConfigs.Returns(_ => db.MLogCategoryConfigs.AsNoTracking());

        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-USER");
        caller.UserId.Returns(11L);

        var cache = new StubCache();
        var sut = new MLogCategoryConfigService(db, roDb, sqids, new StubClock(ClockNow), caller, audit, cache);
        return new Harness(db, sut, audit, sqids, cache);
    }

    /// <summary>UpsertAsync inserts a new row and invalidates the filter cache.</summary>
    [Fact]
    public async Task UpsertAsync_NewCode_InsertsAndInvalidates()
    {
        var h = Create();
        var input = new MLogCategoryConfigInputDto(
            "APPLICATION.RECEIVE", "App receive", true, MLogSeverityFloorDto.Notice);

        var result = await h.Sut.UpsertAsync(input);

        result.IsSuccess.Should().BeTrue();
        (await h.Db.MLogCategoryConfigs.CountAsync()).Should().Be(1);
        h.Cache.InvalidateCount.Should().Be(1);
    }

    /// <summary>UpsertAsync updates existing row by CategoryCode.</summary>
    [Fact]
    public async Task UpsertAsync_ExistingCode_Updates()
    {
        var h = Create();
        await h.Sut.UpsertAsync(new MLogCategoryConfigInputDto(
            "AUTH", "Auth events", true, MLogSeverityFloorDto.Notice));

        var result = await h.Sut.UpsertAsync(new MLogCategoryConfigInputDto(
            "AUTH", "Auth (renamed)", false, MLogSeverityFloorDto.Critical));

        result.IsSuccess.Should().BeTrue();
        (await h.Db.MLogCategoryConfigs.CountAsync()).Should().Be(1);
        var row = await h.Db.MLogCategoryConfigs.SingleAsync();
        row.IsEnabled.Should().BeFalse();
        row.MinSeverity.Should().Be(MLogSeverityFloor.Critical);
    }

    /// <summary>UpsertAsync emits a Critical audit.</summary>
    [Fact]
    public async Task UpsertAsync_EmitsCriticalAudit()
    {
        var h = Create();
        await h.Sut.UpsertAsync(new MLogCategoryConfigInputDto(
            "AUTH", "Auth events", true, MLogSeverityFloorDto.Notice));

        await h.Audit.Received(1).RecordAsync(
            MLogCategoryConfigService.AuditUpserted,
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(MLogCategoryConfig),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>DeactivateAsync flips the row and invalidates cache.</summary>
    [Fact]
    public async Task DeactivateAsync_FlipsAndInvalidates()
    {
        var h = Create();
        await h.Sut.UpsertAsync(new MLogCategoryConfigInputDto(
            "AUTH", "Auth events", true, MLogSeverityFloorDto.Notice));
        var row = await h.Db.MLogCategoryConfigs.SingleAsync();
        var sqid = $"SQID-{row.Id}";
        h.Sqids.TryDecode(sqid).Returns(Result<long>.Success(row.Id));

        var result = await h.Sut.DeactivateAsync(sqid);

        result.IsSuccess.Should().BeTrue();
        (await h.Db.MLogCategoryConfigs.SingleAsync()).IsActive.Should().BeFalse();
        h.Cache.InvalidateCount.Should().BeGreaterThan(1);
    }
}
