using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// Mapping and round-trip tests for the new <c>Notification.DeliveryStatus</c> field
/// (A3 — notification delivery tracking for Annex 6g stats). These tests pin the
/// behaviour BEFORE the entity and configuration are updated (CLAUDE.md RULE 1):
/// <list type="bullet">
///   <item>The column is mapped and stored as <c>integer</c> with a non-clustered index,</item>
///   <item>Fresh entities default to <see cref="NotificationDeliveryStatus.Pending"/>,</item>
///   <item>Every enum value persists and round-trips through EF,</item>
///   <item>The column is non-nullable (the property type is the enum, not <c>Nullable&lt;T&gt;</c>).</item>
/// </list>
/// </summary>
public class NotificationDeliveryStatusTests
{
    /// <summary>Locks the wiring: <c>Notification.DeliveryStatus</c> MUST be mapped as a required integer column.</summary>
    [Fact]
    public void DeliveryStatus_IsMappedAsRequiredInteger()
    {
        using var db = BuildContext(NewDbName());

        var property = db.Model.FindEntityType(typeof(Notification))!
            .FindProperty(nameof(Notification.DeliveryStatus));

        property.Should().NotBeNull(
            "Notification.DeliveryStatus must be mapped by NotificationConfiguration.");
        property!.IsNullable.Should().BeFalse(
            "DeliveryStatus is a value-type enum and must be persisted non-nullable.");
        property.ClrType.Should().Be<NotificationDeliveryStatus>();
    }

    /// <summary>Asserts that an index on the new column exists so the Annex 6g report can group efficiently.</summary>
    [Fact]
    public void DeliveryStatus_HasIndex()
    {
        using var db = BuildContext(NewDbName());

        var entity = db.Model.FindEntityType(typeof(Notification))!;
        entity.GetIndexes().Should().Contain(
            idx => idx.Properties.Count == 1 &&
                   idx.Properties[0].Name == nameof(Notification.DeliveryStatus),
            "the report builder groups by DeliveryStatus and needs a non-clustered index.");
    }

    /// <summary>A freshly-constructed <see cref="Notification"/> defaults to <see cref="NotificationDeliveryStatus.Pending"/>.</summary>
    [Fact]
    public void New_Notification_DefaultsToPending()
    {
        var n = new Notification
        {
            CreatedAtUtc = DateTime.UtcNow,
            RecipientUserId = 1,
            Channel = NotificationChannel.InApp,
            Subject = "s",
            Body = "b",
        };

        n.DeliveryStatus.Should().Be(NotificationDeliveryStatus.Pending);
    }

    /// <summary>Round-trip each enum value through EF to confirm persistence.</summary>
    [Theory]
    [InlineData(NotificationDeliveryStatus.Pending)]
    [InlineData(NotificationDeliveryStatus.Delivered)]
    [InlineData(NotificationDeliveryStatus.Failed)]
    [InlineData(NotificationDeliveryStatus.Suppressed)]
    public async Task DeliveryStatus_RoundTripsThroughEf(NotificationDeliveryStatus status)
    {
        var dbName = NewDbName();
        long persistedId;
        await using (var write = BuildContext(dbName))
        {
            var n = new Notification
            {
                CreatedAtUtc = DateTime.UtcNow,
                RecipientUserId = 42,
                Channel = NotificationChannel.Email,
                Subject = "Round-trip",
                Body = "Body",
                DeliveryStatus = status,
                IsActive = true,
            };
            write.Notifications.Add(n);
            await write.SaveChangesAsync();
            persistedId = n.Id;
        }

        await using var read = BuildContext(dbName);
        var loaded = await read.Notifications.AsNoTracking()
            .SingleAsync(n => n.Id == persistedId);

        loaded.DeliveryStatus.Should().Be(status);
    }

    /// <summary>Unique-per-test in-memory database name.</summary>
    private static string NewDbName() => $"cnas-notif-ds-{Guid.NewGuid():N}";

    /// <summary>Builds a <see cref="CnasDbContext"/> against the named in-memory store.</summary>
    private static CnasDbContext BuildContext(string dbName)
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }
}
