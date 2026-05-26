using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2279 / TOR SEC 033 — maps <see cref="ClassificationCatalogEntry"/> to
/// <c>cnas.ClassificationCatalogEntries</c>. Natural key
/// <c>(SnapshotId, TypeFullName, PropertyName)</c> enforced via composite
/// unique index; secondary indexes back the dashboard's per-label and
/// per-explicit-flag projections.
/// </summary>
public sealed class ClassificationCatalogEntryConfiguration
    : AuditableEntityConfiguration<ClassificationCatalogEntry>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ClassificationCatalogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ClassificationCatalogEntries");

        builder.Property(e => e.SnapshotId).IsRequired();
        builder.Property(e => e.TypeFullName).IsRequired().HasMaxLength(512);
        builder.Property(e => e.PropertyName).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Label).IsRequired().HasMaxLength(32);
        builder.Property(e => e.IsExplicit).IsRequired();
        builder.Property(e => e.DeclaringAssembly).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Notes).HasMaxLength(500);

        builder.HasOne<ClassificationCatalogSnapshot>()
            .WithMany()
            .HasForeignKey(e => e.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.SnapshotId, e.TypeFullName, e.PropertyName })
            .IsUnique()
            .HasDatabaseName("UX_ClassificationCatalogEntries_Snapshot_Type_Property");

        builder.HasIndex(e => e.Label)
            .HasDatabaseName("IX_ClassificationCatalogEntries_Label");

        builder.HasIndex(e => e.IsExplicit)
            .HasDatabaseName("IX_ClassificationCatalogEntries_IsExplicit");
    }
}
