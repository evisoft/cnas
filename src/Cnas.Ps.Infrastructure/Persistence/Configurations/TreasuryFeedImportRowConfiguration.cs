using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1810 / TOR BP 1.2-I — maps <see cref="TreasuryFeedImportRow"/> to
/// <c>cnas.TreasuryFeedImportRows</c>. Unique composite index on
/// <c>(ImportId, RowOrdinal)</c> so the per-import ordinal stays gap-free;
/// secondary index on <c>(Status, ImportId)</c> drives the "failed rows"
/// drill-down on the admin surface.
/// </summary>
public sealed class TreasuryFeedImportRowConfiguration : AuditableEntityConfiguration<TreasuryFeedImportRow>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<TreasuryFeedImportRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TreasuryFeedImportRows");

        builder.Property(e => e.ImportId).IsRequired();
        builder.Property(e => e.RowOrdinal).IsRequired();
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.RawPayloadJson).IsRequired().HasMaxLength(4096);
        builder.Property(e => e.MappedReceiptId);
        builder.Property(e => e.ErrorCode).HasMaxLength(64);
        builder.Property(e => e.ErrorDescription).HasMaxLength(500);
        builder.Property(e => e.ProcessedAt);

        builder.HasIndex(e => new { e.ImportId, e.RowOrdinal })
            .IsUnique()
            .HasDatabaseName("IX_TreasuryFeedImportRows_ImportId_RowOrdinal");

        builder.HasIndex(e => new { e.Status, e.ImportId })
            .HasDatabaseName("IX_TreasuryFeedImportRows_Status_ImportId");
    }
}
