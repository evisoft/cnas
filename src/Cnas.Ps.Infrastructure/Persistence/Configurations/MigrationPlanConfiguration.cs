using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2430 / TOR M4 — maps <see cref="MigrationPlan"/> to
/// <c>cnas.MigrationPlans</c>. Enforces unique <c>PlanCode</c> and indexes
/// <c>(Status, TargetEntityName)</c> for admin-list backing.
/// </summary>
public sealed class MigrationPlanConfiguration : AuditableEntityConfiguration<MigrationPlan>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MigrationPlan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MigrationPlans");

        builder.Property(e => e.PlanCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasMaxLength(2000);
        builder.Property(e => e.SourceKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.TargetEntityName).IsRequired().HasMaxLength(128);
        builder.Property(e => e.MappingDescriptorJson).HasMaxLength(16384);
        builder.Property(e => e.BatchSize).IsRequired().HasDefaultValue(1000);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.RegisteredByUserId).IsRequired();
        builder.Property(e => e.ApprovedByUserId);
        builder.Property(e => e.ApprovedAt);

        builder.HasIndex(e => e.PlanCode)
            .IsUnique()
            .HasDatabaseName("UX_MigrationPlans_PlanCode");

        builder.HasIndex(e => new { e.Status, e.TargetEntityName })
            .HasDatabaseName("IX_MigrationPlans_Status_TargetEntityName");
    }
}
