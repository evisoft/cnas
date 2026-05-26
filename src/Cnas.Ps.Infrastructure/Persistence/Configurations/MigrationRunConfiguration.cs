using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2430 / R2431 / TOR M4 — maps <see cref="MigrationRun"/> to
/// <c>cnas.MigrationRuns</c>. Two indexes back the admin workload: a
/// recency index for the runs list page and a (Status, PlanId) index for
/// per-plan filtering.
/// </summary>
public sealed class MigrationRunConfiguration : AuditableEntityConfiguration<MigrationRun>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MigrationRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MigrationRuns");

        builder.Property(e => e.PlanId).IsRequired();
        builder.Property(e => e.TriggerKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.StartedAt).IsRequired();
        builder.Property(e => e.CompletedAt);
        builder.Property(e => e.TotalSourceRowsSeen).IsRequired();
        builder.Property(e => e.TotalRowsImported).IsRequired();
        builder.Property(e => e.TotalRowsUpdated).IsRequired();
        builder.Property(e => e.TotalRowsSkipped).IsRequired();
        builder.Property(e => e.TotalRowsFailed).IsRequired();
        builder.Property(e => e.FailureReason).HasMaxLength(1000);
        builder.Property(e => e.IsDryRun).IsRequired();

        builder.HasIndex(e => e.StartedAt)
            .IsDescending(true)
            .HasDatabaseName("IX_MigrationRuns_StartedAt_Desc");

        builder.HasIndex(e => new { e.Status, e.PlanId })
            .HasDatabaseName("IX_MigrationRuns_Status_PlanId");
    }
}
