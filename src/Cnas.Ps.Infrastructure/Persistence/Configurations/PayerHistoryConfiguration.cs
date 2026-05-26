using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0301 — maps <see cref="PayerHistory"/> to <c>cnas.PayerHistory</c>. Append-only
/// audit-style log. Index <c>(PayerId, ChangedAtUtc DESC)</c> serves the per-Payer
/// chronological listing endpoint.
/// </summary>
public sealed class PayerHistoryConfiguration : AuditableEntityConfiguration<PayerHistory>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PayerHistory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PayerHistory");

        builder.Property(e => e.PayerId).IsRequired();
        builder.Property(e => e.FieldName).IsRequired().HasMaxLength(100);
        builder.Property(e => e.OldValue).HasMaxLength(2000);
        builder.Property(e => e.NewValue).HasMaxLength(2000);
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.ChangedAtUtc).IsRequired();
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => new { e.PayerId, e.ChangedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_PayerHistory_PayerId_ChangedAtUtcDesc");
    }
}
