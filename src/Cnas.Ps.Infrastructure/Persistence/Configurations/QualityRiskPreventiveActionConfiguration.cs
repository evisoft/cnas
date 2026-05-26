using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2506 / TOR PIR 037-040 — maps <see cref="QualityRiskPreventiveAction"/>
/// to <c>cnas.QualityRiskPreventiveActions</c>. Indexes on
/// <c>(RiskId, Status)</c> and <c>(Status, DueDate)</c>.
/// </summary>
public sealed class QualityRiskPreventiveActionConfiguration
    : AuditableEntityConfiguration<QualityRiskPreventiveAction>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<QualityRiskPreventiveAction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("QualityRiskPreventiveActions");

        builder.Property(e => e.RiskId).IsRequired();
        builder.Property(e => e.Description).IsRequired().HasMaxLength(2000);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.DueDate).IsRequired();
        builder.Property(e => e.AssignedToUserId).IsRequired();
        builder.Property(e => e.CompletedAt);
        builder.Property(e => e.CompletionNote).HasMaxLength(1000);

        builder.HasIndex(e => new { e.RiskId, e.Status })
            .HasDatabaseName("IX_QualityRiskPreventiveActions_RiskId_Status");

        builder.HasIndex(e => new { e.Status, e.DueDate })
            .HasDatabaseName("IX_QualityRiskPreventiveActions_Status_DueDate");
    }
}
