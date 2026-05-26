using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1810 / TOR BP 1.2-I — maps <see cref="TreasuryFeedImport"/> to
/// <c>cnas.TreasuryFeedImports</c>. Two indexes back the admin workload:
/// a filtered unique index on <c>(FeedDate, SourceKind)</c> WHERE
/// <c>Status='Completed'</c> prevents double-ingest of the same day, and a
/// <c>(Status, StartedAt DESC)</c> index supports the list page.
/// </summary>
public sealed class TreasuryFeedImportConfiguration : AuditableEntityConfiguration<TreasuryFeedImport>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<TreasuryFeedImport> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TreasuryFeedImports");

        builder.Property(e => e.FeedDate).IsRequired();
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.SourceKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.SourceReference).HasMaxLength(512);
        builder.Property(e => e.FileSizeBytes);
        builder.Property(e => e.FileHashSha256).HasMaxLength(64);
        builder.Property(e => e.RowsTotal).IsRequired();
        builder.Property(e => e.RowsImported).IsRequired();
        builder.Property(e => e.RowsUpdated).IsRequired();
        builder.Property(e => e.RowsSkipped).IsRequired();
        builder.Property(e => e.RowsFailed).IsRequired();
        builder.Property(e => e.StartedAt).IsRequired();
        builder.Property(e => e.CompletedAt);
        builder.Property(e => e.FailureReason).HasMaxLength(1000);
        builder.Property(e => e.TriggerKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        // Filtered unique index: only one Completed import per (date, source).
        // Failed / Skipped rows for the same date remain permitted so operators
        // can retry an import after fixing whatever caused the prior failure.
        builder.HasIndex(e => new { e.FeedDate, e.SourceKind })
            .IsUnique()
            .HasFilter("\"Status\" = 'Completed'")
            .HasDatabaseName("IX_TreasuryFeedImports_FeedDate_SourceKind_Completed");

        // Admin-list backing index — status filter + recency sort.
        builder.HasIndex(e => new { e.Status, e.StartedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_TreasuryFeedImports_Status_StartedAt");
    }
}
