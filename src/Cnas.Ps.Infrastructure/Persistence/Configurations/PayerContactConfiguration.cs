using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0301 — maps <see cref="PayerContact"/> to <c>cnas.PayerContacts</c>. Filtered unique
/// index enforces single-current-row per Payer.
/// </summary>
public sealed class PayerContactConfiguration : AuditableEntityConfiguration<PayerContact>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PayerContact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PayerContacts");

        builder.Property(e => e.PayerId).IsRequired();
        builder.Property(e => e.PhoneE164).HasMaxLength(32);
        builder.Property(e => e.Email).HasMaxLength(254);
        builder.Property(e => e.ContactPersonName).HasMaxLength(200);
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => e.PayerId);
        builder.HasIndex(e => e.ValidFromUtc);

        builder.HasIndex(e => e.PayerId)
            .HasFilter("\"ValidToUtc\" IS NULL")
            .IsUnique()
            .HasDatabaseName("UX_PayerContacts_CurrentRow");
    }
}
