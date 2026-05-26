using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0913 / TOR BP 2.2-D — maps
/// <see cref="InsuredPersonContributionAdjustment"/> to
/// <c>cnas.InsuredPersonContributionAdjustments</c>. A non-unique index on
/// <c>(InsuredPersonSolicitantId, Month)</c> supports per-citizen monthly
/// listings; uniqueness is intentionally NOT enforced because multiple
/// independent supporting documents may contribute to the same month.
/// </summary>
public sealed class InsuredPersonContributionAdjustmentConfiguration
    : AuditableEntityConfiguration<InsuredPersonContributionAdjustment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<InsuredPersonContributionAdjustment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("InsuredPersonContributionAdjustments");

        builder.Property(e => e.InsuredPersonSolicitantId).IsRequired();
        builder.Property(e => e.Month).IsRequired();
        builder.Property(e => e.AdjustmentAmount).HasPrecision(18, 2);
        builder.Property(e => e.SourceDocumentCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.SourceDocumentReference).HasMaxLength(128);
        builder.Property(e => e.Reason).HasMaxLength(500);

        // Per-citizen monthly listing index.
        builder.HasIndex(e => new { e.InsuredPersonSolicitantId, e.Month })
            .HasDatabaseName("IX_InsuredPersonContributionAdjustments_Solicitant_Month");
    }
}
