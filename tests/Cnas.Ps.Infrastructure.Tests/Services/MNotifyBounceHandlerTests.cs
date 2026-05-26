using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.MNotify;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0115 / TOR CF 14.07 — pins the contract of <see cref="MNotifyBounceHandler"/>.
/// </summary>
public sealed class MNotifyBounceHandlerTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-bounce-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record Harness(CnasDbContext Db, MNotifyBounceHandler Sut, IAuditService Audit);

    private static Harness Create()
    {
        var db = CreateContext();
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-GATEWAY");
        var sut = new MNotifyBounceHandler(db, new StubClock(ClockNow), caller, audit);
        return new Harness(db, sut, audit);
    }

    private static async Task<long> SeedNotificationAsync(Harness h, string correlation = "REF-1")
    {
        var n = new Notification
        {
            CreatedAtUtc = ClockNow,
            RecipientUserId = 42,
            Channel = NotificationChannel.Email,
            DeliveryStatus = NotificationDeliveryStatus.Delivered,
            Subject = "Hello",
            Body = "Body",
            CorrelationId = correlation,
            IsActive = true,
        };
        h.Db.Notifications.Add(n);
        await h.Db.SaveChangesAsync();
        return n.Id;
    }

    /// <summary>Lookup ok → DeliveryStatus is flipped to Failed.</summary>
    [Fact]
    public async Task HandleBounceAsync_KnownReference_FlipsStatus()
    {
        var h = Create();
        var id = await SeedNotificationAsync(h);
        var payload = new MNotifyBounceWebhookPayload("REF-1", "MAILBOX_FULL", "queue limit", ClockNow);

        var result = await h.Sut.HandleBounceAsync(payload);

        result.IsSuccess.Should().BeTrue();
        var row = await h.Db.Notifications.SingleAsync(n => n.Id == id);
        row.DeliveryStatus.Should().Be(NotificationDeliveryStatus.Failed);
    }

    /// <summary>Unknown reference returns NotFound.</summary>
    [Fact]
    public async Task HandleBounceAsync_UnknownReference_ReturnsNotFound()
    {
        var h = Create();
        var payload = new MNotifyBounceWebhookPayload("MISSING", "BLOCKED", null, ClockNow);

        var result = await h.Sut.HandleBounceAsync(payload);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>Successful flip emits the NOTIFY.BOUNCED audit.</summary>
    [Fact]
    public async Task HandleBounceAsync_OnSuccess_EmitsAudit()
    {
        var h = Create();
        await SeedNotificationAsync(h);
        var payload = new MNotifyBounceWebhookPayload("REF-1", "MAILBOX_FULL", "queue limit", ClockNow);

        var result = await h.Sut.HandleBounceAsync(payload);

        result.IsSuccess.Should().BeTrue();
        await h.Audit.Received(1).RecordAsync(
            MNotifyBounceHandler.AuditBounced,
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(Notification),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Replay against an already-Failed row is an idempotent success without re-audit.</summary>
    [Fact]
    public async Task HandleBounceAsync_AlreadyFailed_IsIdempotent()
    {
        var h = Create();
        await SeedNotificationAsync(h);
        var payload = new MNotifyBounceWebhookPayload("REF-1", "MAILBOX_FULL", null, ClockNow);

        var first = await h.Sut.HandleBounceAsync(payload);
        var second = await h.Sut.HandleBounceAsync(payload);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        // Audit recorded once (on the first call).
        await h.Audit.Received(1).RecordAsync(
            MNotifyBounceHandler.AuditBounced,
            Arg.Any<AuditSeverity>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
