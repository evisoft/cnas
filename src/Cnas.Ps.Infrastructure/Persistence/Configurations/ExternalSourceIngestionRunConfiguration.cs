using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0203 / TOR CF 20.06 — maps <see cref="ExternalSourceIngestionRun"/> to
/// <c>cnas.ExternalSourceIngestionRuns</c>. Two non-unique indexes back the
/// admin workload (per-source recency + status filter) and one unique index
/// on <see cref="ExternalSourceIngestionRun.RunNumber"/> guarantees the
/// per-year monotonic counter stays stable.
/// </summary>
public sealed class ExternalSourceIngestionRunConfiguration
    : AuditableEntityConfiguration<ExternalSourceIngestionRun>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ExternalSourceIngestionRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ExternalSourceIngestionRuns");

        builder.Property(e => e.SourceCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.RunNumber).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.TriggerKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.StartedAtUtc).IsRequired();
        builder.Property(e => e.CompletedAtUtc);
        builder.Property(e => e.TotalRecordsPulled).IsRequired();
        builder.Property(e => e.TotalRecordsApplied).IsRequired();
        builder.Property(e => e.TotalRecordsSkipped).IsRequired();
        builder.Property(e => e.TotalRecordsFailed).IsRequired();
        builder.Property(e => e.FailureReason).HasMaxLength(1000);
        builder.Property(e => e.UpstreamPullId).HasMaxLength(128);

        // Natural-key uniqueness — RunNumber is the human-friendly business id.
        builder.HasIndex(e => e.RunNumber)
            .IsUnique()
            .HasDatabaseName("IX_ExternalSourceIngestionRuns_RunNumber");

        // Admin-list per-source recency index.
        builder.HasIndex(e => new { e.SourceCode, e.StartedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_ExternalSourceIngestionRuns_SourceCode_StartedAtUtc");

        // Admin-list status filter index.
        builder.HasIndex(e => new { e.Status, e.StartedAtUtc })
            .HasDatabaseName("IX_ExternalSourceIngestionRuns_Status_StartedAtUtc");
    }
}
