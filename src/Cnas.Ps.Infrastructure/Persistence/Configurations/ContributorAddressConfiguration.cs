using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0311 — maps <see cref="ContributorAddress"/> to <c>cnas.ContributorAddresses</c>.
/// Filtered unique index <c>(ContributorId) WHERE ValidToUtc IS NULL</c> enforces
/// single-current-row per Contributor.
/// </summary>
public sealed class ContributorAddressConfiguration : AuditableEntityConfiguration<ContributorAddress>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContributorAddress> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContributorAddresses");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.Street).IsRequired().HasMaxLength(200);
        builder.Property(e => e.City).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Region).IsRequired().HasMaxLength(200);
        builder.Property(e => e.PostalCode).IsRequired().HasMaxLength(10);
        builder.Property(e => e.Country).IsRequired().HasMaxLength(2).HasDefaultValue("MD");
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        // R0805 / Annex 1 §8.1.1.6 — additive kind discriminator + building / apartment
        // components. Encryption of the PII columns (BuildingNumber, Apartment) is wired
        // in CnasDbContext.OnModelCreating below; the AES-GCM envelope minimum is 43
        // characters even for a single-byte plaintext, so the column widths are sized to
        // 128 to accommodate the ciphertext envelope for plaintexts up to the documented
        // ~32-char operational maximum.
        builder.Property(e => e.BuildingNumber).HasMaxLength(128);
        builder.Property(e => e.Apartment).HasMaxLength(128);
        builder.Property(e => e.AddressKind).HasConversion<int?>();

        builder.HasIndex(e => e.ContributorId);
        builder.HasIndex(e => e.ValidFromUtc);

        builder.HasIndex(e => e.ContributorId)
            .HasFilter("\"ValidToUtc\" IS NULL")
            .IsUnique()
            .HasDatabaseName("UX_ContributorAddresses_CurrentRow");
    }
}
