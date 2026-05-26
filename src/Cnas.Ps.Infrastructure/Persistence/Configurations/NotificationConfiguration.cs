using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Notification"/> to <c>cnas.Notifications</c>.</summary>
public sealed class NotificationConfiguration : AuditableEntityConfiguration<Notification>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.Property(n => n.Channel).IsRequired().HasConversion<int>();

        // DeliveryStatus mirrors the Channel mapping (enum stored as int). The
        // default value lets back-fill migrations and clean inserts that omit the
        // column land on Pending (= 0) without an explicit assignment from the writer.
        builder.Property(n => n.DeliveryStatus)
            .IsRequired()
            .HasConversion<int>()
            .HasDefaultValue(NotificationDeliveryStatus.Pending);

        builder.Property(n => n.Subject).IsRequired().HasMaxLength(256);
        builder.Property(n => n.Body).IsRequired();
        builder.Property(n => n.CorrelationId).HasMaxLength(64);

        // R0172 / TOR CF 22.05 — related-entity columns. Capped at 64 chars
        // because the vocabulary is a closed set of CLR type names (see
        // NotificationRelatedEntityTypes). The composite index lets the
        // future "related-business-object inbox" filter slice without a
        // sequential scan.
        builder.Property(n => n.RelatedEntityType).HasMaxLength(64);
        builder.HasIndex(n => new { n.RelatedEntityType, n.RelatedEntityId });

        builder.HasIndex(n => new { n.RecipientUserId, n.ReadAtUtc });
        builder.HasIndex(n => n.CorrelationId);

        // Non-clustered index supporting the Annex 6g RPT-NOTIFICATIONS-DELIVERY
        // GROUP BY DeliveryStatus aggregation.
        builder.HasIndex(n => n.DeliveryStatus);
    }
}
