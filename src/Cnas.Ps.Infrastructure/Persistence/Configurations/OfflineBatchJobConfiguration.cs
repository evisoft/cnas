using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2161 / TOR INT 002 — maps <see cref="OfflineBatchJob"/> to
/// <c>cnas.OfflineBatchJobs</c>. Stores <see cref="OfflineBatchJob.Kind"/> +
/// <see cref="OfflineBatchJob.Status"/> as stable strings so future renames
/// surface at compile time; indexes <c>(SubmittedByUserId, SubmittedAtUtc)</c>
/// for the per-caller status-list backing.
/// </summary>
public sealed class OfflineBatchJobConfiguration : AuditableEntityConfiguration<OfflineBatchJob>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<OfflineBatchJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OfflineBatchJobs");

        builder.Property(e => e.Kind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(e => e.SubmittedByUserId).IsRequired();
        builder.Property(e => e.SubmittedAtUtc).IsRequired();
        builder.Property(e => e.StartedAtUtc);
        builder.Property(e => e.CompletedAtUtc);
        builder.Property(e => e.ErrorMessage).HasMaxLength(2000);
        builder.Property(e => e.ResultBlobKey).HasMaxLength(512);
        builder.Property(e => e.RowCount).IsRequired();

        builder.HasIndex(e => new { e.SubmittedByUserId, e.SubmittedAtUtc })
            .HasDatabaseName("IX_OfflineBatchJobs_SubmittedByUser_SubmittedAt");

        builder.HasIndex(e => e.Status)
            .HasDatabaseName("IX_OfflineBatchJobs_Status");
    }
}
