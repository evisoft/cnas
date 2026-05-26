using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.MessageBus;
using Cnas.Ps.Infrastructure.Tests.MGov;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.MessageBus;

/// <summary>
/// R0103 / TOR CF 14.02 — unit tests for
/// <see cref="IntegrationEventDeduper"/>. These tests run against the EF Core
/// InMemory provider; the production race-on-unique-constraint branch is
/// covered indirectly via the
/// <c>IsUniqueViolation</c> helper (the InMemory provider does not enforce
/// unique indexes, so the probe-before-insert path is the operational
/// branch in tests).
/// </summary>
public class IntegrationEventDeduperTests
{
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-dedup-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static IntegrationEventDeduper CreateSut(CnasDbContext db, TestClock? clock = null)
    {
        clock ??= new TestClock();
        return new IntegrationEventDeduper(db, db, clock, NullLogger<IntegrationEventDeduper>.Instance);
    }

    [Fact]
    public async Task TryClaimAsync_FirstObservation_InsertsRow_AndReturnsNotAlreadyProcessed()
    {
        using var db = CreateContext();
        var sut = CreateSut(db);

        var result = await sut.TryClaimAsync("msg-001", "cnas-ps", "md.cnas.ps.test.v1");

        result.IsSuccess.Should().BeTrue();
        result.Value.AlreadyProcessed.Should().BeFalse();
        result.Value.MessageId.Should().Be("msg-001");
        result.Value.EarlierProcessedAtUtc.Should().BeNull();

        var row = await db.ProcessedIntegrationEvents.SingleAsync(e => e.MessageId == "msg-001");
        row.Source.Should().Be("cnas-ps");
        row.Type.Should().Be("md.cnas.ps.test.v1");
        row.Outcome.Should().Be(ProcessedEventOutcome.Accepted);
    }

    [Fact]
    public async Task TryClaimAsync_SecondCallSameMessageId_ReturnsAlreadyProcessed_WithEarlierTimestamp()
    {
        using var db = CreateContext();
        var clock = new TestClock();
        var sut = CreateSut(db, clock);

        var first = await sut.TryClaimAsync("msg-002", "cnas-ps", "md.cnas.ps.test.v1");
        first.IsSuccess.Should().BeTrue();
        var earlierAt = clock.UtcNow;

        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        var second = await sut.TryClaimAsync("msg-002", "cnas-ps", "md.cnas.ps.test.v1");

        second.IsSuccess.Should().BeTrue();
        second.Value.AlreadyProcessed.Should().BeTrue();
        second.Value.EarlierProcessedAtUtc.Should().Be(earlierAt);

        var rows = await db.ProcessedIntegrationEvents.CountAsync(e => e.MessageId == "msg-002");
        rows.Should().Be(1);
    }

    [Fact]
    public async Task TryClaimAsync_EmptyMessageId_ReturnsValidationFailed()
    {
        using var db = CreateContext();
        var sut = CreateSut(db);

        var result = await sut.TryClaimAsync(string.Empty, "cnas-ps", "md.cnas.ps.test.v1");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await db.ProcessedIntegrationEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MarkFailedAsync_FlipsOutcome_ToFailed_AndPersistsReason()
    {
        using var db = CreateContext();
        var sut = CreateSut(db);

        await sut.TryClaimAsync("msg-003", "cnas-ps", "md.cnas.ps.test.v1");
        var result = await sut.MarkFailedAsync("msg-003", "downstream handler threw");

        result.IsSuccess.Should().BeTrue();
        var row = await db.ProcessedIntegrationEvents.SingleAsync(e => e.MessageId == "msg-003");
        row.Outcome.Should().Be(ProcessedEventOutcome.Failed);
        row.FailureReason.Should().Be("downstream handler threw");
    }

    [Fact]
    public async Task MarkFailedAsync_UnknownMessageId_ReturnsNotFound()
    {
        using var db = CreateContext();
        var sut = CreateSut(db);

        var result = await sut.MarkFailedAsync("msg-no-such-row", "anything");

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task IsKnownAsync_ReturnsTrue_ForExistingMessageId()
    {
        using var db = CreateContext();
        var sut = CreateSut(db);

        await sut.TryClaimAsync("msg-004", "cnas-ps", "md.cnas.ps.test.v1");
        var result = await sut.IsKnownAsync("msg-004");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
    }

    [Fact]
    public async Task IsKnownAsync_ReturnsFalse_ForUnknownMessageId()
    {
        using var db = CreateContext();
        var sut = CreateSut(db);

        var result = await sut.IsKnownAsync("msg-unknown");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
    }

    [Fact]
    public async Task IsKnownAsync_EmptyMessageId_ReturnsValidationFailed()
    {
        using var db = CreateContext();
        var sut = CreateSut(db);

        var result = await sut.IsKnownAsync(string.Empty);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }
}
