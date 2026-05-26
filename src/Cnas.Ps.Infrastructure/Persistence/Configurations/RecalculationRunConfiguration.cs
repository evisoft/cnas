using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1503 / TOR §3.7-D — maps <see cref="RecalculationRun"/> to
/// <c>cnas.RecalculationRuns</c>. Descending index on <c>StartedAt</c> for the
/// recent-runs view; secondary index on <see cref="RecalculationRun.LegalChangeEventId"/>
/// for the per-event drill-down.
/// </summary>
public sealed class RecalculationRunConfiguration : AuditableEntityConfiguration<RecalculationRun>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<RecalculationRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RecalculationRuns");

        builder.Property(r => r.LegalChangeEventId).IsRequired();
        builder.Property(r => r.TriggerKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(r => r.Mode)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(r => r.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(r => r.StartedAt).IsRequired();
        builder.Property(r => r.CompletedAt);
        builder.Property(r => r.TotalDecisionsScanned).IsRequired();
        builder.Property(r => r.TotalDecisionsRecalculated).IsRequired();
        builder.Property(r => r.TotalSkipped).IsRequired();
        builder.Property(r => r.TotalFailed).IsRequired();
        builder.Property(r => r.TotalDeltaMdl).HasPrecision(18, 2).IsRequired();
        builder.Property(r => r.FailureReason).HasMaxLength(2000);

        builder.HasIndex(r => r.StartedAt)
            .IsDescending()
            .HasDatabaseName("IX_RecalculationRuns_StartedAt");

        builder.HasIndex(r => r.LegalChangeEventId)
            .HasDatabaseName("IX_RecalculationRuns_LegalChangeEventId");
    }
}
