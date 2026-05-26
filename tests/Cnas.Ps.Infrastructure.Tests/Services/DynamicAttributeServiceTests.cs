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
/// R2190-R2200 / TOR §15.6 FLEX 006 — TDD coverage for
/// <see cref="DynamicAttributeService"/>. Backed by an EF Core InMemory
/// store; asserts the upsert / fetch / list contract from
/// <see cref="IDynamicAttributeService"/> and the allow-list rejection path.
/// </summary>
public sealed class DynamicAttributeServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-dyn-attr-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static DynamicAttributeService Build(CnasDbContext db)
    {
        var sqids = Substitute.For<ISqidService>();
        // Round-trip stub: Encode(N) => "SQID-N"; TryDecode("SQID-N") => N
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var s = call.Arg<string?>();
            if (s is not null
                && s.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(s.AsSpan(5), out var v))
            {
                return Result<long>.Success(v);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });

        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);

        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-ADMIN");

        return new DynamicAttributeService(db, sqids, clock, caller);
    }

    [Fact]
    public async Task SetAsync_HappyPath_InsertsAndReturnsRow()
    {
        using var db = CreateContext();
        var sut = Build(db);

        var input = new SetEntityAttributeInputDto(
            EntityType: "Application",
            EntitySqid: "SQID-42",
            AttributeCode: "priority",
            Value: "high");

        var result = await sut.SetAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.EntityType.Should().Be("Application");
        result.Value.EntitySqid.Should().Be("SQID-42");
        result.Value.AttributeCode.Should().Be("priority");
        result.Value.Value.Should().Be("high");

        var rows = await db.EntityAttributeValues.ToListAsync();
        rows.Should().ContainSingle();
        rows[0].EntityType.Should().Be("Application");
        rows[0].EntityId.Should().Be(42);
        rows[0].Value.Should().Be("high");
    }

    [Fact]
    public async Task SetAsync_ExistingRow_UpdatesValue()
    {
        using var db = CreateContext();
        var sut = Build(db);

        var first = new SetEntityAttributeInputDto("Application", "SQID-42", "priority", "low");
        var second = new SetEntityAttributeInputDto("Application", "SQID-42", "priority", "high");

        (await sut.SetAsync(first, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var updated = await sut.SetAsync(second, CancellationToken.None);

        updated.IsSuccess.Should().BeTrue();
        updated.Value.Value.Should().Be("high");

        var rows = await db.EntityAttributeValues.ToListAsync();
        rows.Should().ContainSingle("upsert must not insert a duplicate row");
        rows[0].Value.Should().Be("high");
    }

    [Fact]
    public async Task GetAsync_KnownTuple_ReturnsRow()
    {
        using var db = CreateContext();
        var sut = Build(db);

        await sut.SetAsync(
            new SetEntityAttributeInputDto("Application", "SQID-7", "tag", "vip,review"),
            CancellationToken.None);

        var result = await sut.GetAsync("Application", "SQID-7", "tag", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be("vip,review");
    }

    [Fact]
    public async Task ListAsync_ReturnsAllAttributesForEntity()
    {
        using var db = CreateContext();
        var sut = Build(db);

        await sut.SetAsync(new SetEntityAttributeInputDto("Application", "SQID-3", "priority", "high"), CancellationToken.None);
        await sut.SetAsync(new SetEntityAttributeInputDto("Application", "SQID-3", "tag", "audit"), CancellationToken.None);
        await sut.SetAsync(new SetEntityAttributeInputDto("Application", "SQID-99", "priority", "low"), CancellationToken.None);

        var result = await sut.ListAsync("Application", "SQID-3", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Select(r => r.AttributeCode).Should().BeEquivalentTo(["priority", "tag"]);
    }

    [Fact]
    public async Task SetAsync_DisallowedAttributeCode_ReturnsValidationFailed()
    {
        using var db = CreateContext();
        var sut = Build(db);

        var input = new SetEntityAttributeInputDto(
            EntityType: "Application",
            EntitySqid: "SQID-42",
            AttributeCode: "ssn", // NOT in the allow-list
            Value: "12345");

        var result = await sut.SetAsync(input, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await db.EntityAttributeValues.AnyAsync()).Should().BeFalse(
            "rejection must not persist any row");
    }

    [Fact]
    public async Task GetAsync_UnknownTuple_ReturnsNotFound()
    {
        using var db = CreateContext();
        var sut = Build(db);

        var result = await sut.GetAsync("Application", "SQID-42", "priority", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
