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
/// Integration tests for the Annex 6j report <c>RPT-NOTIFICATIONS-BY-CITIZEN</c> — top-N
/// citizens (recipients) by <see cref="Notification"/> count whose
/// <see cref="AuditableEntity.CreatedAtUtc"/> falls in the half-open UTC window
/// <c>[fromUtc, toUtc)</c>. Soft-deleted notifications are excluded; rows are ordered by Count
/// desc, then Username (Ordinal). Soft-deleted / unresolved recipients land in a
/// <c>user#{id}</c> sentinel.
/// </summary>
public class RptNotificationsByCitizenTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-NOTIFICATIONS-BY-CITIZEN";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Recipient Username,Notification Count");
    }

    /// <summary>
    /// Seeds three notifications to alice, two to bob, and one to carol — all in window — plus
    /// one out-of-window for alice (excluded) and one soft-deleted for bob (excluded). Verifies
    /// Count desc ordering with stable Ordinal tie-breaks.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsByRecipientAndOrdersByCountDesc()
    {
        var harness = Harness.Create();
        var alice = await harness.SeedRecipientAsync("alice");
        var bob = await harness.SeedRecipientAsync("bob");
        var carol = await harness.SeedRecipientAsync("carol");
        await harness.SeedNotificationAsync(alice, ClockNow.AddDays(-1));
        await harness.SeedNotificationAsync(alice, ClockNow.AddDays(-2));
        await harness.SeedNotificationAsync(alice, ClockNow.AddDays(-3));
        await harness.SeedNotificationAsync(bob, ClockNow.AddDays(-4));
        await harness.SeedNotificationAsync(bob, ClockNow.AddDays(-5));
        await harness.SeedNotificationAsync(carol, ClockNow.AddDays(-6));
        // Out-of-window — excluded.
        await harness.SeedNotificationAsync(alice, ClockNow.AddDays(-100));
        // Soft-deleted in-window — excluded.
        await harness.SeedNotificationAsync(bob, ClockNow.AddDays(-7), isActive: false);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 recipient rows.
        lines.Should().HaveCount(4);
        lines[1].Should().Be("alice,3");
        lines[2].Should().Be("bob,2");
        lines[3].Should().Be("carol,1");
    }

    /// <summary>An empty window emits only the header row.</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsOnlyHeader()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
    }

    /// <summary>
    /// Edge case — a notification whose recipient profile is soft-deleted falls back to the
    /// <c>user#{id}</c> sentinel rather than being silently dropped, preserving the
    /// "rows sum to total volume" invariant.
    /// </summary>
    [Fact]
    public async Task Execute_SoftDeletedRecipient_FallsBackToSentinelBucket()
    {
        var harness = Harness.Create();
        var ghost = await harness.SeedRecipientAsync("ghost", recipientActive: false);
        await harness.SeedNotificationAsync(ghost, ClockNow.AddDays(-1));
        await harness.SeedNotificationAsync(ghost, ClockNow.AddDays(-2));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 1 sentinel row.
        lines.Should().HaveCount(2);
        lines[1].Should().Be($"user#{ghost},2");
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

    /// <summary>Builds the parameters JSON for the [fromUtc, toUtc) half-open window.</summary>
    private static string BuildParams(DateTime fromUtc, DateTime toUtc)
        => $"{{ \"fromUtc\": \"{fromUtc.ToString("O", CultureInfo.InvariantCulture)}\", " +
           $"\"toUtc\": \"{toUtc.ToString("O", CultureInfo.InvariantCulture)}\" }}";

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

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-notif-cit-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a recipient <see cref="UserProfile"/> and returns its id.</summary>
        public async Task<long> SeedRecipientAsync(string localLogin, bool recipientActive = true)
        {
            var u = new UserProfile
            {
                CreatedAtUtc = ClockNow,
                LocalLogin = localLogin,
                DisplayName = localLogin,
                IsActive = recipientActive,
            };
            Db.UserProfiles.Add(u);
            await Db.SaveChangesAsync();
            return u.Id;
        }

        /// <summary>Seeds a <see cref="Notification"/> for the supplied recipient with the supplied creation timestamp.</summary>
        public async Task SeedNotificationAsync(long recipientUserId, DateTime createdAtUtc, bool isActive = true)
        {
            Db.Notifications.Add(new Notification
            {
                CreatedAtUtc = createdAtUtc,
                RecipientUserId = recipientUserId,
                Channel = NotificationChannel.Email,
                Subject = "Subject",
                Body = "Body",
                IsActive = isActive,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
