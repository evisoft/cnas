using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports;

/// <summary>
/// Integration tests for the Annex 6g report <c>RPT-NOTIFICATIONS-DELIVERY</c> — per-channel
/// notification delivery counts (Delivered / Failed / Suppressed) inside the UTC window
/// <c>[fromUtc, toUtc)</c>. Source: <see cref="Notification"/> rows created in the window,
/// grouped by the authoritative <see cref="Notification.DeliveryStatus"/> field.
/// <see cref="NotificationDeliveryStatus.Pending"/> rows are deliberately NOT counted in
/// any column — they have not yet reached an outcome.
/// </summary>
public class RptNotificationsDeliveryTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-NOTIFICATIONS-DELIVERY";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Channel,Delivered Count,Failed Count,Suppressed Count");
    }

    /// <summary>
    /// Seeds two Delivered and one Failed Email notifications in window, plus an SMS Delivered
    /// and an InApp Failed. Verifies the per-channel counts grouped by the new
    /// <see cref="Notification.DeliveryStatus"/> field.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsByChannelWithDeliveryState()
    {
        var harness = Harness.Create();
        // Email: 2 delivered, 1 failed.
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            createdUtc: ClockNow.AddDays(-1), status: NotificationDeliveryStatus.Delivered,
            dispatchedUtc: ClockNow.AddDays(-1).AddMinutes(5));
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            createdUtc: ClockNow.AddDays(-2), status: NotificationDeliveryStatus.Delivered,
            dispatchedUtc: ClockNow.AddDays(-2).AddMinutes(2));
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            createdUtc: ClockNow.AddDays(-3), status: NotificationDeliveryStatus.Failed);
        // SMS: 1 delivered.
        await harness.SeedNotificationAsync(NotificationChannel.Sms,
            createdUtc: ClockNow.AddDays(-4), status: NotificationDeliveryStatus.Delivered,
            dispatchedUtc: ClockNow.AddDays(-4).AddMinutes(1));
        // InApp: 1 failed.
        await harness.SeedNotificationAsync(NotificationChannel.InApp,
            createdUtc: ClockNow.AddDays(-5), status: NotificationDeliveryStatus.Failed);
        // Out-of-window — excluded.
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            createdUtc: ClockNow.AddDays(-100), status: NotificationDeliveryStatus.Delivered,
            dispatchedUtc: ClockNow.AddDays(-100).AddMinutes(1));

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("Email,2,1,0");
        lines.Should().Contain("SMS,1,0,0");
        lines.Should().Contain("InApp,0,1,0");
    }

    /// <summary>
    /// Seeds Delivered-only rows on the Email channel. Only the Delivered count column populates;
    /// Failed and Suppressed stay at zero. Pins the new authoritative
    /// <see cref="Notification.DeliveryStatus"/> contract.
    /// </summary>
    [Fact]
    public async Task Execute_DeliveredStatus_PopulatesOnlyDeliveredColumn()
    {
        var harness = Harness.Create();
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            createdUtc: ClockNow.AddDays(-1), status: NotificationDeliveryStatus.Delivered,
            dispatchedUtc: ClockNow.AddDays(-1).AddMinutes(1));
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            createdUtc: ClockNow.AddDays(-2), status: NotificationDeliveryStatus.Delivered,
            dispatchedUtc: ClockNow.AddDays(-2).AddMinutes(1));

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("Email,2,0,0");
    }

    /// <summary>
    /// Seeds Failed-only rows on the SMS channel. Only the Failed count column populates.
    /// </summary>
    [Fact]
    public async Task Execute_FailedStatus_PopulatesOnlyFailedColumn()
    {
        var harness = Harness.Create();
        await harness.SeedNotificationAsync(NotificationChannel.Sms,
            createdUtc: ClockNow.AddDays(-1), status: NotificationDeliveryStatus.Failed);
        await harness.SeedNotificationAsync(NotificationChannel.Sms,
            createdUtc: ClockNow.AddDays(-2), status: NotificationDeliveryStatus.Failed);
        await harness.SeedNotificationAsync(NotificationChannel.Sms,
            createdUtc: ClockNow.AddDays(-3), status: NotificationDeliveryStatus.Failed);

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("SMS,0,3,0");
    }

    /// <summary>
    /// Seeds Suppressed-only rows on the InApp channel. Only the Suppressed count column populates.
    /// Pre-fix this assertion was RED — the old builder had <c>const long suppressed = 0L</c>
    /// and could never emit a non-zero value in that column.
    /// </summary>
    [Fact]
    public async Task Execute_SuppressedStatus_PopulatesOnlySuppressedColumn()
    {
        var harness = Harness.Create();
        await harness.SeedNotificationAsync(NotificationChannel.InApp,
            createdUtc: ClockNow.AddDays(-1), status: NotificationDeliveryStatus.Suppressed);
        await harness.SeedNotificationAsync(NotificationChannel.InApp,
            createdUtc: ClockNow.AddDays(-2), status: NotificationDeliveryStatus.Suppressed);

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("InApp,0,0,2");
    }

    /// <summary>
    /// Seeds Pending-only rows. None of the three count columns may move — a Pending row has
    /// not yet reached an outcome. Pre-fix this assertion was RED — the old heuristic treated
    /// a null <see cref="Notification.DispatchedAtUtc"/> as Failed, which would have populated
    /// the Failed column instead.
    /// </summary>
    [Fact]
    public async Task Execute_PendingStatus_DoesNotPopulateAnyOutcomeColumn()
    {
        var harness = Harness.Create();
        await harness.SeedNotificationAsync(NotificationChannel.Email,
            createdUtc: ClockNow.AddDays(-1), status: NotificationDeliveryStatus.Pending);
        await harness.SeedNotificationAsync(NotificationChannel.Sms,
            createdUtc: ClockNow.AddDays(-1), status: NotificationDeliveryStatus.Pending);
        await harness.SeedNotificationAsync(NotificationChannel.InApp,
            createdUtc: ClockNow.AddDays(-1), status: NotificationDeliveryStatus.Pending);

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("Email,0,0,0");
        lines.Should().Contain("SMS,0,0,0");
        lines.Should().Contain("InApp,0,0,0");
    }

    /// <summary>Every channel is emitted densely even when the window carries no notifications.</summary>
    [Fact]
    public async Task Execute_DenseChannels_EmitsAllThreeEvenWhenEmpty()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("Email,0,0,0");
        lines.Should().Contain("SMS,0,0,0");
        lines.Should().Contain("InApp,0,0,0");
    }

    /// <summary>Missing window parameters must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingParameters_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

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

        /// <summary>Monotonic recipient counter so synthetic recipients don't collide.</summary>
        private long _recipientCounter;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-notif-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>
        /// Seeds a <see cref="Notification"/> on the supplied channel with the supplied
        /// creation timestamp and explicit delivery status. <paramref name="dispatchedUtc"/>
        /// is optional and only meaningful when <paramref name="status"/> is
        /// <see cref="NotificationDeliveryStatus.Delivered"/>; it is informational only —
        /// the report builder groups exclusively by <see cref="Notification.DeliveryStatus"/>.
        /// </summary>
        public async Task SeedNotificationAsync(
            NotificationChannel channel,
            DateTime createdUtc,
            NotificationDeliveryStatus status,
            DateTime? dispatchedUtc = null)
        {
            _recipientCounter++;
            Db.Notifications.Add(new Notification
            {
                CreatedAtUtc = createdUtc,
                RecipientUserId = _recipientCounter,
                Channel = channel,
                Subject = $"Subject-{_recipientCounter}",
                Body = $"Body-{_recipientCounter}",
                DeliveryStatus = status,
                DispatchedAtUtc = dispatchedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
