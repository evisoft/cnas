using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1710 / TOR INT 002 — maps <see cref="OfflineBatchRow"/> to
/// <c>cnas.OfflineBatchRows</c>. Unique composite index on
/// (<c>SubmissionId</c>, <c>RowOrdinal</c>) so the per-submission row
/// numbering stays gap-free; secondary index on <c>Status</c> for the
/// dashboard "failed rows" filter.
/// </summary>
public sealed class OfflineBatchRowConfiguration : AuditableEntityConfiguration<OfflineBatchRow>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<OfflineBatchRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OfflineBatchRows");

        builder.Property(e => e.SubmissionId).IsRequired();
        builder.Property(e => e.RowOrdinal).IsRequired();
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.RequestPayloadJson).IsRequired().HasMaxLength(4096);
        builder.Property(e => e.ResponsePayloadJson).HasMaxLength(8192);
        builder.Property(e => e.ErrorCode).HasMaxLength(64);
        builder.Property(e => e.ErrorDescription).HasMaxLength(500);
        builder.Property(e => e.ProcessedAt);

        builder.HasIndex(e => new { e.SubmissionId, e.RowOrdinal })
            .IsUnique()
            .HasDatabaseName("IX_OfflineBatchRows_SubmissionId_RowOrdinal");

        builder.HasIndex(e => e.Status)
            .HasDatabaseName("IX_OfflineBatchRows_Status");
    }
}
