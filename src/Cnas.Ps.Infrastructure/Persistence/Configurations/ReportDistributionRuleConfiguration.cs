using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1906 / TOR Annex 6 — maps <see cref="ReportDistributionRule"/> to
/// <c>cnas.ReportDistributionRules</c>. Enforces the composite unique
/// index (ReportCode, Channel, RecipientKind, RecipientCodeHash,
/// RecipientCode) so an admin cannot accidentally register two identical
/// rules. The unique index includes BOTH the hash and the raw recipient
/// column — the hash is populated for emails (which are encrypted, so the
/// raw column carries random ciphertext) while the raw column carries the
/// small opaque code for the other recipient kinds.
/// </summary>
public sealed class ReportDistributionRuleConfiguration : AuditableEntityConfiguration<ReportDistributionRule>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ReportDistributionRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReportDistributionRules");

        builder.Property(e => e.ReportCode).IsRequired().HasMaxLength(64);

        builder.Property(e => e.Channel)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.RecipientKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        // RecipientCode is encrypted at rest by the global converter wiring in
        // CnasDbContext.OnModelCreating; the underlying column carries the
        // ciphertext envelope which may be longer than the plaintext cap.
        builder.Property(e => e.RecipientCode)
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(e => e.RecipientCodeHash).HasMaxLength(44);

        builder.Property(e => e.Format)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.Priority)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.EffectiveFrom).IsRequired();
        builder.Property(e => e.EffectiveUntil);
        builder.Property(e => e.CreatedByUserId).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(1000);

        builder.HasIndex(e => e.ReportCode)
            .HasDatabaseName("IX_ReportDistributionRules_ReportCode");

        builder.HasIndex(e => new { e.ReportCode, e.Channel, e.IsActive })
            .HasDatabaseName("IX_ReportDistributionRules_ReportCode_Channel_IsActive");

        // Composite uniqueness across the full identifying tuple. Includes the
        // hash column so two EmailAddress rules with the same canonicalised
        // address cannot be registered for the same report + channel + kind.
        builder.HasIndex(e => new { e.ReportCode, e.Channel, e.RecipientKind, e.RecipientCodeHash, e.RecipientCode })
            .IsUnique()
            .HasDatabaseName("UX_ReportDistributionRules_NaturalKey");
    }
}
