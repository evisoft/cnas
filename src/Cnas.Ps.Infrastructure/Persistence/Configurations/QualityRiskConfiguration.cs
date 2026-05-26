using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2506 / TOR PIR 037-040 — maps <see cref="QualityRisk"/> to
/// <c>cnas.QualityRisks</c>. Enforces unique <c>RiskCode</c> and indexes on
/// <c>(Status, Category)</c> and <c>(LastReviewedAt)</c>.
/// </summary>
public sealed class QualityRiskConfiguration : AuditableEntityConfiguration<QualityRisk>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<QualityRisk> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("QualityRisks");

        builder.Property(e => e.RiskCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).IsRequired().HasMaxLength(4000);
        builder.Property(e => e.Category)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Likelihood)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Impact)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.OwnerUserId).IsRequired();
        builder.Property(e => e.IdentifiedAt).IsRequired();
        builder.Property(e => e.LastReviewedAt);
        builder.Property(e => e.ClosedAt);
        builder.Property(e => e.ClosureReason).HasMaxLength(1000);

        builder.HasIndex(e => e.RiskCode)
            .IsUnique()
            .HasDatabaseName("UX_QualityRisks_RiskCode");

        builder.HasIndex(e => new { e.Status, e.Category })
            .HasDatabaseName("IX_QualityRisks_Status_Category");

        builder.HasIndex(e => e.LastReviewedAt)
            .HasDatabaseName("IX_QualityRisks_LastReviewedAt");
    }
}
