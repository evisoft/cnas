using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2282 / TOR SEC 036 — maps <see cref="IntegrityCheckFinding"/> to
/// <c>cnas.IntegrityCheckFindings</c>. Indexes back the three dashboard
/// projections:
/// <list type="bullet">
///   <item><c>(RunId)</c> — the per-run drill-down view.</item>
///   <item><c>(CheckCode, AggregateRowId)</c> — the cross-run dedupe projection.</item>
///   <item><c>(Severity, Acknowledged)</c> — the open-findings dashboard tile.</item>
///   <item><c>(FirstDetectedAt DESC)</c> — the recency sort fall-back.</item>
/// </list>
/// </summary>
public sealed class IntegrityCheckFindingConfiguration : AuditableEntityConfiguration<IntegrityCheckFinding>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<IntegrityCheckFinding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IntegrityCheckFindings");

        builder.Property(e => e.RunId).IsRequired();
        builder.Property(e => e.CheckCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Severity)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.AggregateName).IsRequired().HasMaxLength(128);
        builder.Property(e => e.AggregateRowId).IsRequired();
        builder.Property(e => e.Description).IsRequired().HasMaxLength(1000);
        builder.Property(e => e.ExpectedValue).HasMaxLength(256);
        builder.Property(e => e.ActualValue).HasMaxLength(256);
        builder.Property(e => e.FirstDetectedAt).IsRequired();
        builder.Property(e => e.Acknowledged).IsRequired();
        builder.Property(e => e.AcknowledgedAt);
        builder.Property(e => e.AcknowledgedByUserId);
        builder.Property(e => e.AcknowledgementNote).HasMaxLength(1000);

        builder.HasOne<IntegrityCheckRun>()
            .WithMany()
            .HasForeignKey(e => e.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.RunId)
            .HasDatabaseName("IX_IntegrityCheckFindings_RunId");

        builder.HasIndex(e => new { e.CheckCode, e.AggregateRowId })
            .HasDatabaseName("IX_IntegrityCheckFindings_CheckCode_AggregateRowId");

        builder.HasIndex(e => new { e.Severity, e.Acknowledged })
            .HasDatabaseName("IX_IntegrityCheckFindings_Severity_Acknowledged");

        builder.HasIndex(e => e.FirstDetectedAt)
            .IsDescending()
            .HasDatabaseName("IX_IntegrityCheckFindings_FirstDetectedAt");
    }
}
