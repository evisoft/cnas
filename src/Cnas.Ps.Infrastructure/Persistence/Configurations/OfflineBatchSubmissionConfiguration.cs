using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1710 / TOR INT 002 — maps <see cref="OfflineBatchSubmission"/> to
/// <c>cnas.OfflineBatchSubmissions</c>. Three indexes back the consumer +
/// admin lookup workloads: a unique index on <c>BatchNumber</c>, a
/// (Status DESC, SubmittedAt DESC) index for the queue, and a
/// (ConsumerSubject, SubmittedAt DESC) index for the per-consumer history.
/// </summary>
public sealed class OfflineBatchSubmissionConfiguration : AuditableEntityConfiguration<OfflineBatchSubmission>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<OfflineBatchSubmission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OfflineBatchSubmissions");

        builder.Property(e => e.BatchNumber).IsRequired().HasMaxLength(32);
        builder.Property(e => e.ConsumerSubject).IsRequired().HasMaxLength(128);
        builder.Property(e => e.OpCode)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(64);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.RequestFileName).IsRequired().HasMaxLength(256);
        builder.Property(e => e.RequestFileSizeBytes).IsRequired();
        builder.Property(e => e.RequestFileHashSha256).IsRequired().HasMaxLength(64);
        builder.Property(e => e.RequestFileStorageKey).IsRequired().HasMaxLength(256);
        builder.Property(e => e.RequestRowCount).IsRequired();
        builder.Property(e => e.ResponseFileStorageKey).HasMaxLength(256);
        builder.Property(e => e.ResponseFileHashSha256).HasMaxLength(64);
        builder.Property(e => e.ResponseFileSignatureBase64).HasMaxLength(128);
        builder.Property(e => e.SubmittedAt).IsRequired();
        builder.Property(e => e.StartedAt);
        builder.Property(e => e.CompletedAt);
        builder.Property(e => e.FailureReason).HasMaxLength(1000);
        builder.Property(e => e.TotalRowsProcessed).IsRequired();
        builder.Property(e => e.TotalRowsFailed).IsRequired();

        builder.HasIndex(e => e.BatchNumber)
            .IsUnique()
            .HasDatabaseName("IX_OfflineBatchSubmissions_BatchNumber");

        builder.HasIndex(e => new { e.Status, e.SubmittedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_OfflineBatchSubmissions_Status_SubmittedAt");

        builder.HasIndex(e => new { e.ConsumerSubject, e.SubmittedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_OfflineBatchSubmissions_ConsumerSubject_SubmittedAt");

        builder.HasIndex(e => new { e.Status, e.OpCode })
            .HasDatabaseName("IX_OfflineBatchSubmissions_Status_OpCode");
    }
}
