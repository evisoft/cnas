using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0311 — maps <see cref="ContributorContact"/> to <c>cnas.ContributorContacts</c>.
/// Filtered unique index enforces single-current-row per Contributor.
/// </summary>
public sealed class ContributorContactConfiguration : AuditableEntityConfiguration<ContributorContact>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContributorContact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContributorContacts");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.PhoneE164).HasMaxLength(32);
        builder.Property(e => e.Email).HasMaxLength(254);
        builder.Property(e => e.ContactPersonName).HasMaxLength(200);
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        // R0805 / Annex 1 §8.1.1.6 — additive kind-typed contact column.
        // Encryption is wired in CnasDbContext.OnModelCreating below; the
        // AES-GCM envelope (12-byte nonce + 16-byte tag base64-wrapped around
        // up to a 254-char email) exceeds the prior 256-char column width, so
        // the storage cap is widened to 512 to accommodate the ciphertext
        // envelope for any plaintext that satisfies the per-kind limits
        // documented on ContactKind.
        builder.Property(e => e.Value).HasMaxLength(512);
        builder.Property(e => e.ContactKind).HasConversion<int?>();

        builder.HasIndex(e => e.ContributorId);
        builder.HasIndex(e => e.ValidFromUtc);

        builder.HasIndex(e => e.ContributorId)
            .HasFilter("\"ValidToUtc\" IS NULL")
            .IsUnique()
            .HasDatabaseName("UX_ContributorContacts_CurrentRow");
    }
}
