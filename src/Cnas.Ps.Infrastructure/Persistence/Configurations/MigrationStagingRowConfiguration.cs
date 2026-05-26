using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2431 / TOR M4 — maps <see cref="MigrationStagingRow"/> to
/// <c>cnas.MigrationStagingRows</c>. Three indexes back the importer +
/// admin workload: a unique <c>(RunId, BatchOrdinal, RowOrdinalInBatch)</c>
/// to guard idempotent re-projection, a <c>(TargetEntityName, IsCommitted)</c>
/// index used by the future per-entity commit step, and a
/// <c>TargetEntityKey</c> index used by the reconciler.
/// </summary>
public sealed class MigrationStagingRowConfiguration : AuditableEntityConfiguration<MigrationStagingRow>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MigrationStagingRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MigrationStagingRows");

        builder.Property(e => e.RunId).IsRequired();
        builder.Property(e => e.BatchOrdinal).IsRequired();
        builder.Property(e => e.RowOrdinalInBatch).IsRequired();
        builder.Property(e => e.TargetEntityName).IsRequired().HasMaxLength(128);
        builder.Property(e => e.TargetEntityKey).IsRequired().HasMaxLength(256);
        builder.Property(e => e.MappedFieldsJson).IsRequired().HasMaxLength(16384);
        builder.Property(e => e.SourceFingerprint).IsRequired().HasMaxLength(128);
        builder.Property(e => e.IsCommitted).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.CommittedAt);

        builder.HasIndex(e => new { e.RunId, e.BatchOrdinal, e.RowOrdinalInBatch })
            .IsUnique()
            .HasDatabaseName("UX_MigrationStagingRows_RunId_BatchOrdinal_RowOrdinal");

        builder.HasIndex(e => new { e.TargetEntityName, e.IsCommitted })
            .HasDatabaseName("IX_MigrationStagingRows_TargetEntity_IsCommitted");

        builder.HasIndex(e => e.TargetEntityKey)
            .HasDatabaseName("IX_MigrationStagingRows_TargetEntityKey");
    }
}
