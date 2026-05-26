using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0301 — maps <see cref="PayerActivityCAEM"/> to <c>cnas.PayerActivityCAEM</c>.
/// Filtered unique index is on <c>(PayerId, CaemCode) WHERE ValidToUtc IS NULL</c> —
/// the same CAEM code may not be active twice concurrently, but multiple distinct
/// CAEM codes are permitted simultaneously (primary + secondaries).
/// </summary>
public sealed class PayerActivityCAEMConfiguration : AuditableEntityConfiguration<PayerActivityCAEM>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PayerActivityCAEM> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PayerActivityCAEM");

        builder.Property(e => e.PayerId).IsRequired();
        builder.Property(e => e.CaemCode).IsRequired().HasMaxLength(16);
        builder.Property(e => e.CaemDescription).IsRequired().HasMaxLength(500);
        builder.Property(e => e.IsPrimary).IsRequired();
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => e.PayerId);
        builder.HasIndex(e => e.ValidFromUtc);

        builder.HasIndex(e => new { e.PayerId, e.CaemCode })
            .HasFilter("\"ValidToUtc\" IS NULL")
            .IsUnique()
            .HasDatabaseName("UX_PayerActivityCAEM_CurrentRow");
    }
}
