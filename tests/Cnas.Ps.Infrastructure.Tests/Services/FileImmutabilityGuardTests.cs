using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0137 — round-trip tests for <see cref="FileImmutabilityMarker"/> +
/// <see cref="FileImmutabilityGuard"/>. The pair operates over a small EF-Core
/// in-memory store and an injected <see cref="ICnasTimeProvider"/> so every
/// behaviour observable to callers is pinned without touching real MinIO.
/// </summary>
public sealed class FileImmutabilityGuardTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);
    private const string Bucket = "documents";
    private const string ObjectKey = "2026/05/24/abc123";

    /// <summary>Builds a fresh harness (DB context + marker + guard) for a single test.</summary>
    private static Harness NewHarness()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-immut-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new CnasDbContext(opts);
        var clock = new StubClock(ClockNow);
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns((long?)42);

        var marker = new FileImmutabilityMarker(
            db, clock, caller, NullLogger<FileImmutabilityMarker>.Instance);
        var guard = new FileImmutabilityGuard(
            db, NullLogger<FileImmutabilityGuard>.Instance);

        return new Harness(db, marker, guard);
    }

    [Fact]
    public async Task Mark_ThenCheck_RoundTrips_DeleteIsRefused()
    {
        var h = NewHarness();

        var mark = await h.Marker.MarkImmutableAsync(Bucket, ObjectKey, "test");
        mark.IsSuccess.Should().BeTrue();

        var check = await h.Guard.CheckBeforeDeleteAsync(Bucket, ObjectKey);
        check.IsFailure.Should().BeTrue();
        check.ErrorCode.Should().Be(ErrorCodes.ImmutableObject);
    }

    [Fact]
    public async Task Check_WhenNotMarked_ReturnsSuccess()
    {
        var h = NewHarness();

        var check = await h.Guard.CheckBeforeDeleteAsync(Bucket, ObjectKey);

        check.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Mark_WhenAlreadyMarked_IsIdempotent()
    {
        var h = NewHarness();

        var first = await h.Marker.MarkImmutableAsync(Bucket, ObjectKey, "first");
        var second = await h.Marker.MarkImmutableAsync(Bucket, ObjectKey, "second");

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        var rows = await h.Db.FileImmutabilityRecords.CountAsync();
        rows.Should().Be(1);
    }

    [Fact]
    public async Task ListImmutable_ReturnsAllMarkedObjects()
    {
        var h = NewHarness();
        await h.Marker.MarkImmutableAsync(Bucket, ObjectKey, "a");
        await h.Marker.MarkImmutableAsync(Bucket, "2026/05/24/def456", "b");
        await h.Marker.MarkImmutableAsync("other-bucket", "x/y/z", "c");

        var list = await h.Marker.ListImmutableAsync(Bucket);

        list.Should().HaveCount(2);
        list.Should().Contain(ObjectKey);
        list.Should().Contain("2026/05/24/def456");
    }

    [Fact]
    public async Task Mark_WithBlankBucket_FailsValidation()
    {
        var h = NewHarness();

        var result = await h.Marker.MarkImmutableAsync(" ", ObjectKey, null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Check_WithBlankObjectKey_FailsValidation()
    {
        var h = NewHarness();

        var result = await h.Guard.CheckBeforeDeleteAsync(Bucket, " ");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    private sealed record Harness(
        CnasDbContext Db,
        FileImmutabilityMarker Marker,
        FileImmutabilityGuard Guard);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }
}
