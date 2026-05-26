using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2282 / TOR SEC 036 — maps <see cref="IntegrityCheckRun"/> to
/// <c>cnas.IntegrityCheckRuns</c>. Two indexes back the dashboard: descending
/// start-time for the recent-runs view, and status for the "in-flight runs"
/// filter.
/// </summary>
public sealed class IntegrityCheckRunConfiguration : AuditableEntityConfiguration<IntegrityCheckRun>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<IntegrityCheckRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IntegrityCheckRuns");

        builder.Property(e => e.RunStartedAt).IsRequired();
        builder.Property(e => e.RunCompletedAt);
        builder.Property(e => e.TriggerKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.TotalRowsScanned).IsRequired();
        builder.Property(e => e.TotalFindings).IsRequired();
        builder.Property(e => e.FindingsBySeverity).HasMaxLength(512);
        builder.Property(e => e.FailureReason).HasMaxLength(2000);

        builder.HasIndex(e => e.RunStartedAt)
            .IsDescending()
            .HasDatabaseName("IX_IntegrityCheckRuns_RunStartedAt");

        builder.HasIndex(e => e.Status)
            .HasDatabaseName("IX_IntegrityCheckRuns_Status");
    }
}
