using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Audit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — tests for
/// <see cref="EntityHistoryService"/>. Verifies the timeline projection over
/// the <c>EntityHistoryRow</c> registry.
/// </summary>
public sealed class EntityHistoryServiceTests
{
    private static (CnasDbContext writer, IReadOnlyCnasDbContext read, ISqidService sqids) NewFixture()
    {
        var sqids = new SqidService(Options.Create(new SqidOptions
        {
            Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            MinLength = 6,
        }));
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"history-svc-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new CnasDbContext(opts);
        return (db, db, sqids);
    }

    [Fact]
    public async Task Timeline_Empty_ReturnsEmptyRowsSuccessfully()
    {
        var (writer, read, sqids) = NewFixture();
        var svc = new EntityHistoryService(read, sqids);

        var result = await svc.GetHistoryAsync(
            entityType: nameof(UserProfile),
            entitySqid: sqids.Encode(42L),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Rows.Should().BeEmpty();
        result.Value.EntityType.Should().Be(nameof(UserProfile));
    }

    [Fact]
    public async Task Timeline_MultiRow_OrderedByChangedAtUtcDescending()
    {
        var (writer, read, sqids) = NewFixture();
        const long entityId = 7L;
        var t0 = new DateTime(2026, 5, 24, 8, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);

        writer.EntityHistoryRows.AddRange(
            new EntityHistoryRow
            {
                EntityType = nameof(UserProfile),
                EntityId = entityId,
                ChangedAtUtc = t0,
                Operation = "I",
                PayloadJson = "{\"DisplayName\":\"v0\"}",
                CreatedAtUtc = t0,
            },
            new EntityHistoryRow
            {
                EntityType = nameof(UserProfile),
                EntityId = entityId,
                ChangedAtUtc = t1,
                Operation = "U",
                PayloadJson = "{\"DisplayName\":\"v1\"}",
                CreatedAtUtc = t1,
            },
            new EntityHistoryRow
            {
                EntityType = nameof(UserProfile),
                EntityId = entityId,
                ChangedAtUtc = t2,
                Operation = "U",
                PayloadJson = "{\"DisplayName\":\"v2\"}",
                CreatedAtUtc = t2,
            });
        await writer.SaveChangesAsync(CancellationToken.None);

        var svc = new EntityHistoryService(read, sqids);

        var result = await svc.GetHistoryAsync(
            entityType: nameof(UserProfile),
            entitySqid: sqids.Encode(entityId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Rows.Should().HaveCount(3);
        result.Value.Rows[0].ChangedAtUtc.Should().Be(t2);
        result.Value.Rows[1].ChangedAtUtc.Should().Be(t1);
        result.Value.Rows[2].ChangedAtUtc.Should().Be(t0);
    }

    [Fact]
    public async Task Timeline_InvalidSqid_ReturnsInvalidSqid()
    {
        var (_, read, sqids) = NewFixture();
        var svc = new EntityHistoryService(read, sqids);

        var result = await svc.GetHistoryAsync(
            entityType: nameof(UserProfile),
            entitySqid: "###not-a-sqid###",
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task Timeline_EmptyType_ReturnsNotFound()
    {
        var (_, read, sqids) = NewFixture();
        var svc = new EntityHistoryService(read, sqids);

        var result = await svc.GetHistoryAsync(
            entityType: "",
            entitySqid: sqids.Encode(1L),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
