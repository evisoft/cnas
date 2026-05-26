using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — tests for
/// <see cref="HistoryTrackingInterceptor"/>. Verifies that
/// <c>IHistoryTracked</c> entities produce the expected I/U/D history rows,
/// that non-tracked entities are skipped, and that PII columns are redacted
/// from the persisted payload.
/// </summary>
public sealed class HistoryTrackingInterceptorTests
{
    private sealed class FixedClock : ICnasTimeProvider
    {
        public DateTime UtcNow { get; set; }
            = new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);
    }

    private static (CnasDbContext db, FixedClock clock) NewContextWithInterceptor()
    {
        var clock = new FixedClock();
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-ADMIN");
        var interceptor = new HistoryTrackingInterceptor(
            NullLogger<HistoryTrackingInterceptor>.Instance, clock, caller);

        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"history-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor)
            .Options;
        return (new CnasDbContext(opts), clock);
    }

    [Fact]
    public async Task Insert_HistoryTracked_ProducesIRow_WithPostImagePayload()
    {
        var (db, clock) = NewContextWithInterceptor();

        var user = new UserProfile
        {
            DisplayName = "Alice",
            CreatedAtUtc = clock.UtcNow,
        };
        db.UserProfiles.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);

        var history = await db.EntityHistoryRows.AsNoTracking().ToListAsync();
        history.Should().ContainSingle(r =>
            r.EntityType == nameof(UserProfile)
            && r.EntityId == user.Id
            && r.Operation == "I"
            && r.ChangedAtUtc == clock.UtcNow);
        history[0].PayloadJson.Should().Contain("DisplayName");
        history[0].PayloadJson.Should().Contain("Alice");
        history[0].ActorSqid.Should().Be("SQID-ADMIN");
    }

    [Fact]
    public async Task Update_HistoryTracked_ProducesURow()
    {
        var (db, _) = NewContextWithInterceptor();

        var user = new UserProfile { DisplayName = "Original" };
        db.UserProfiles.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);

        user.DisplayName = "Renamed";
        await db.SaveChangesAsync(CancellationToken.None);

        var history = await db.EntityHistoryRows.AsNoTracking()
            .Where(r => r.EntityId == user.Id)
            .ToListAsync();
        history.Should().HaveCount(2);
        var update = history.FirstOrDefault(r => r.Operation == "U");
        update.Should().NotBeNull();
        update!.PayloadJson.Should().Contain("Renamed");
    }

    [Fact]
    public async Task Delete_HistoryTracked_ProducesDRow_WithPreImage()
    {
        var (db, _) = NewContextWithInterceptor();

        var user = new UserProfile { DisplayName = "WillDelete" };
        db.UserProfiles.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);
        var userId = user.Id;

        db.UserProfiles.Remove(user);
        await db.SaveChangesAsync(CancellationToken.None);

        var deleteRow = await db.EntityHistoryRows.AsNoTracking()
            .Where(r => r.EntityId == userId && r.Operation == "D")
            .FirstOrDefaultAsync();
        deleteRow.Should().NotBeNull();
        // Pre-image: the original DisplayName value must be in the payload.
        deleteRow!.PayloadJson.Should().Contain("WillDelete");
    }

    [Fact]
    public async Task NonHistoryTracked_Skipped()
    {
        var (db, _) = NewContextWithInterceptor();

        // Notification is NOT IHistoryTracked → no row.
        db.Notifications.Add(new Notification
        {
            RecipientUserId = 1L,
            Channel = NotificationChannel.InApp,
            Subject = "x",
            Body = "y",
        });
        await db.SaveChangesAsync(CancellationToken.None);

        var rows = await db.EntityHistoryRows.AsNoTracking().ToListAsync();
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task PiiColumns_Redacted_FromPayload()
    {
        var (db, _) = NewContextWithInterceptor();

        // UserProfile carries NationalId + Email which sit on the
        // ExcludedPropertyNames backstop list — both must NOT leak into the
        // history payload. The PiiRedactor second-pass also catches values
        // whose JSON key matches a PII substring.
        var user = new UserProfile
        {
            DisplayName = "Bob",
            NationalId = "2000123456782",
            Email = "bob@example.com",
        };
        db.UserProfiles.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);

        var row = await db.EntityHistoryRows.AsNoTracking().FirstAsync();
        row.PayloadJson.Should().NotContain("2000123456782");
        row.PayloadJson.Should().NotContain("bob@example.com");
        // DisplayName is fine to retain.
        row.PayloadJson.Should().Contain("Bob");
    }
}
