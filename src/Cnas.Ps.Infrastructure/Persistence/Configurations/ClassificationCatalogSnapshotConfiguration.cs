using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2279 / TOR SEC 033 — maps <see cref="ClassificationCatalogSnapshot"/> to
/// <c>cnas.ClassificationCatalogSnapshots</c>. Two indexes back the dashboard:
/// descending capture-time for the recent-snapshots view, and status for the
/// "in-flight snapshots" filter.
/// </summary>
public sealed class ClassificationCatalogSnapshotConfiguration
    : AuditableEntityConfiguration<ClassificationCatalogSnapshot>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ClassificationCatalogSnapshot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ClassificationCatalogSnapshots");

        builder.Property(e => e.CapturedAt).IsRequired();
        builder.Property(e => e.TriggerKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.TotalTypesScanned).IsRequired();
        builder.Property(e => e.TotalPropertiesClassified).IsRequired();
        builder.Property(e => e.TotalPropertiesUnclassified).IsRequired();
        builder.Property(e => e.LabelCountsJson).HasMaxLength(2048);
        builder.Property(e => e.AssemblyVersionsJson).HasMaxLength(2048);
        builder.Property(e => e.FailureReason).HasMaxLength(1000);

        builder.HasIndex(e => e.CapturedAt)
            .IsDescending()
            .HasDatabaseName("IX_ClassificationCatalogSnapshots_CapturedAt");

        builder.HasIndex(e => e.Status)
            .HasDatabaseName("IX_ClassificationCatalogSnapshots_Status");
    }
}
