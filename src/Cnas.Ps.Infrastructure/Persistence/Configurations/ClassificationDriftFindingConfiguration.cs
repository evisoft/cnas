using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2279 / TOR SEC 033 — maps <see cref="ClassificationDriftFinding"/> to
/// <c>cnas.ClassificationDriftFindings</c>. Three indexes back the dashboard:
/// the (baseline, current) pair lookup used by the idempotent re-run path,
/// the drift-kind filter, and the open-findings recency sort.
/// </summary>
public sealed class ClassificationDriftFindingConfiguration
    : AuditableEntityConfiguration<ClassificationDriftFinding>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ClassificationDriftFinding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ClassificationDriftFindings");

        builder.Property(e => e.BaselineSnapshotId).IsRequired();
        builder.Property(e => e.CurrentSnapshotId).IsRequired();
        builder.Property(e => e.DriftKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.TypeFullName).IsRequired().HasMaxLength(512);
        builder.Property(e => e.PropertyName).IsRequired().HasMaxLength(128);
        builder.Property(e => e.BaselineLabel).HasMaxLength(32);
        builder.Property(e => e.CurrentLabel).HasMaxLength(32);
        builder.Property(e => e.Acknowledged).IsRequired();
        builder.Property(e => e.AcknowledgedByUserId);
        builder.Property(e => e.AcknowledgedAt);
        builder.Property(e => e.AcknowledgementNote).HasMaxLength(1000);
        builder.Property(e => e.DetectedAt).IsRequired();

        builder.HasOne<ClassificationCatalogSnapshot>()
            .WithMany()
            .HasForeignKey(e => e.BaselineSnapshotId)
            .OnDelete(DeleteBehavior.Restrict);

        // EF needs unique nav-property roots for two FKs against the same principal —
        // configure the second relationship via HasForeignKey without a Navigation.
        builder.HasOne<ClassificationCatalogSnapshot>()
            .WithMany()
            .HasForeignKey(e => e.CurrentSnapshotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.BaselineSnapshotId, e.CurrentSnapshotId })
            .HasDatabaseName("IX_ClassificationDriftFindings_BaselineId_CurrentId");

        builder.HasIndex(e => e.DriftKind)
            .HasDatabaseName("IX_ClassificationDriftFindings_DriftKind");

        builder.HasIndex(e => new { e.Acknowledged, e.DetectedAt })
            .HasDatabaseName("IX_ClassificationDriftFindings_Acknowledged_DetectedAt");
    }
}
