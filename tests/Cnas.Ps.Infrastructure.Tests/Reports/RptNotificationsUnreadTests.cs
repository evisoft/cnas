using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports;

/// <summary>
/// Integration tests for the Annex 6i report <c>RPT-NOTIFICATIONS-UNREAD</c> — per-channel
/// count of <see cref="Notification"/> rows that were dispatched on or before
/// <c>asOfUtc</c> but have not yet been read. Output is dense — Email / SMS / InApp rows are
/// always emitted in that order, even when a channel has zero unread.
/// </summary>
public class RptNotificationsUnreadTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-NOTIFICATIONS-UNREAD";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Channel,Unread Count");
    }

    /// <summary>
    /// Seeds two Email + one InApp dispatched-but-unread; one SMS that has been read (excluded);
    /// one Email that was never dispatched (excluded); one InApp dispatched after the asOf
    /// moment (excluded); one soft-deleted Email unread (excluded). Verifies the dense-row
    /// contract: SMS appears with zero count.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsByChannelWithDenseBuckets()
    {
        var harness = Harness.Create();
        // Two Email unread — counts.
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            dispatchedAtUtc: ClockNow.AddDays(-1), readAtUtc: null);
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            dispatchedAtUtc: ClockNow.AddDays(-2), readAtUtc: null);
        // One InApp unread — counts.
        await harness.SeedNotificationAsync(NotificationChannel.InApp,
            dispatchedAtUtc: ClockNow.AddDays(-3), readAtUtc: null);
        // SMS dispatched and read — excluded.
        await harness.SeedNotificationAsync(NotificationChannel.Sms,
            dispatchedAtUtc: ClockNow.AddDays(-4), readAtUtc: ClockNow.AddDays(-3));
        // Email never dispatched — excluded (not yet visible to recipient).
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            dispatchedAtUtc: null, readAtUtc: null);
        // InApp dispatched after asOf moment — excluded.
        await harness.SeedNotificationAsync(NotificationChannel.InApp,
            dispatchedAtUtc: ClockNow.AddDays(1), readAtUtc: null);
        // Soft-deleted Email unread — excluded.
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            dispatchedAtUtc: ClockNow.AddDays(-1), readAtUtc: null, isActive: false);

        var paramsJson = BuildParams(ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 dense channel rows.
        lines.Should().HaveCount(4);
        lines.Should().Contain("Email,2");
        lines.Should().Contain("SMS,0");
        lines.Should().Contain("InApp,1");
    }

    /// <summary>Every channel bucket is emitted densely with a zero count when no notifications exist.</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsAllThreeChannelRowsWithZero()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(4);
        lines.Should().Contain("Email,0");
        lines.Should().Contain("SMS,0");
        lines.Should().Contain("InApp,0");
    }

    /// <summary>
    /// Edge case — every notification across every channel has been read; the report still
    /// emits the dense three-row shape with all-zero counts.
    /// </summary>
    [Fact]
    public async Task Execute_AllRead_EmitsZeroForEveryChannel()
    {
        var harness = Harness.Create();
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            dispatchedAtUtc: ClockNow.AddDays(-2), readAtUtc: ClockNow.AddDays(-1));
        await harness.SeedNotificationAsync(NotificationChannel.Sms,
            dispatchedAtUtc: ClockNow.AddDays(-2), readAtUtc: ClockNow.AddDays(-1));
        await harness.SeedNotificationAsync(NotificationChannel.InApp,
            dispatchedAtUtc: ClockNow.AddDays(-2), readAtUtc: ClockNow.AddDays(-1));

        var paramsJson = BuildParams(ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(4);
        lines.Should().Contain("Email,0");
        lines.Should().Contain("SMS,0");
        lines.Should().Contain("InApp,0");
    }

    /// <summary>Missing <c>asOfUtc</c> parameter must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingParameters_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>Builds the parameters JSON for the asOf moment.</summary>
    private static string BuildParams(DateTime asOfUtc)
        => $"{{ \"asOfUtc\": \"{asOfUtc.ToString("O", CultureInfo.InvariantCulture)}\" }}";

    /// <summary>Reads the full text of a stream using UTF-8 with BOM detection.</summary>
    private static string ReadAllText(Stream stream)
    {
        stream.Position = 0;
        using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return sr.ReadToEnd();
    }

    /// <summary>Deterministic stub clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Test harness composing EF Core InMemory + ReportingService.</summary>
    private sealed class Harness
    {
        /// <summary>The in-memory database context.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>The system under test.</summary>
        public required ReportingService Service { get; init; }

        /// <summary>Monotonic recipient counter so synthetic rows do not collide.</summary>
        private long _recipientCounter;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-notif-unread-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a single <see cref="Notification"/> with the supplied dispatch / read timestamps.</summary>
        public async Task SeedNotificationAsync(
            NotificationChannel channel,
            DateTime? dispatchedAtUtc,
            DateTime? readAtUtc,
            bool isActive = true)
        {
            _recipientCounter++;
            Db.Notifications.Add(new Notification
            {
                CreatedAtUtc = dispatchedAtUtc ?? ClockNow.AddDays(-5),
                RecipientUserId = _recipientCounter,
                Channel = channel,
                Subject = $"Subj-{_recipientCounter:D3}",
                Body = "Body",
                DispatchedAtUtc = dispatchedAtUtc,
                ReadAtUtc = readAtUtc,
                IsActive = isActive,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
