using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0301 — maps <see cref="PayerAddress"/> to <c>cnas.PayerAddresses</c>. Carries the
/// filtered unique index <c>(PayerId) WHERE ValidToUtc IS NULL</c> that enforces the
/// "exactly one current address row per Payer" invariant at the database level.
/// </summary>
public sealed class PayerAddressConfiguration : AuditableEntityConfiguration<PayerAddress>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PayerAddress> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PayerAddresses");

        builder.Property(e => e.PayerId).IsRequired();
        builder.Property(e => e.Street).IsRequired().HasMaxLength(200);
        builder.Property(e => e.City).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Region).IsRequired().HasMaxLength(200);
        builder.Property(e => e.PostalCode).IsRequired().HasMaxLength(10);
        builder.Property(e => e.Country).IsRequired().HasMaxLength(2).HasDefaultValue("MD");
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => e.PayerId);
        builder.HasIndex(e => e.ValidFromUtc);

        // Filtered unique index — at most one row per Payer with ValidToUtc IS NULL.
        // The Npgsql provider materialises this as a partial index; the InMemory test
        // provider IGNORES the filter and treats every row as eligible for uniqueness
        // (documented limitation; the service layer enforces the invariant programmatically).
        builder.HasIndex(e => e.PayerId)
            .HasFilter("\"ValidToUtc\" IS NULL")
            .IsUnique()
            .HasDatabaseName("UX_PayerAddresses_CurrentRow");
    }
}
