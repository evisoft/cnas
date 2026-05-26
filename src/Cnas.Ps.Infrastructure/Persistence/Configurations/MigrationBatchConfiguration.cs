using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2430 / R2431 / TOR M4 — maps <see cref="MigrationBatch"/> to
/// <c>cnas.MigrationBatches</c>. Enforces uniqueness on
/// <c>(RunId, BatchOrdinal)</c>.
/// </summary>
public sealed class MigrationBatchConfiguration : AuditableEntityConfiguration<MigrationBatch>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MigrationBatch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MigrationBatches");

        builder.Property(e => e.RunId).IsRequired();
        builder.Property(e => e.BatchOrdinal).IsRequired();
        builder.Property(e => e.RowsInBatch).IsRequired();
        builder.Property(e => e.RowsImported).IsRequired();
        builder.Property(e => e.RowsUpdated).IsRequired();
        builder.Property(e => e.RowsSkipped).IsRequired();
        builder.Property(e => e.RowsFailed).IsRequired();
        builder.Property(e => e.DurationMs).IsRequired();
        builder.Property(e => e.ProcessedAt).IsRequired();

        builder.HasIndex(e => new { e.RunId, e.BatchOrdinal })
            .IsUnique()
            .HasDatabaseName("UX_MigrationBatches_RunId_BatchOrdinal");
    }
}
