using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Solicitant"/> to the <c>cnas.Solicitants</c> table.</summary>
public sealed class SolicitantConfiguration : AuditableEntityConfiguration<Solicitant>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Solicitant> builder)
    {
        builder.ToTable("Solicitants");

        // NationalId is encrypted at rest (CLAUDE.md §5.7) — ciphertext is wider than the
        // 13-char plaintext (v1: + base64 of ~41 bytes ≈ 59 chars). The column must be wide
        // enough to hold the envelope. VARCHAR(128) leaves comfortable headroom for any
        // future v2 envelope without another migration.
        builder.Property(s => s.NationalId).IsRequired().HasMaxLength(128);
        builder.Property(s => s.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(s => s.Email).HasMaxLength(320);
        builder.Property(s => s.PhoneE164).HasMaxLength(32);
        builder.Property(s => s.PreferredLanguage).IsRequired().HasMaxLength(8);
        builder.Property(s => s.PostalAddress).HasMaxLength(512);
        builder.Property(s => s.AffiliatedLegalEntityId).HasMaxLength(13);
        // ISO-13616 caps IBAN at 34 characters; the upstream country prefix + checksum + BBAN.
        builder.Property(s => s.BankIban).HasMaxLength(34);

        // Shadow column for equality lookups on the encrypted NationalId. Base64(HMAC-SHA256)
        // is exactly 44 chars (32 bytes + '=' padding). See Solicitant.NationalIdHash.
        builder.Property(s => s.NationalIdHash).IsRequired().HasMaxLength(44);

        // The plaintext column is encrypted — every row encrypts to different ciphertext, so
        // an index on it is useless for equality and bloated for ordering. The unique index
        // moves to NationalIdHash; we do NOT keep any index on the plaintext column.
        builder.HasIndex(s => s.NationalIdHash).IsUnique();

        // R0671 / CF 18.06 — region code drives the IAccessScope filter. 16 chars matches
        // the short codes the directory carries today (e.g. "CHIS", "BLT", "BAL"); index is
        // a plain B-tree because the filter is an equality / IN match.
        builder.Property(s => s.RegionCode).HasMaxLength(16);
        builder.HasIndex(s => s.RegionCode);
    }
}
