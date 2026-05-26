using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2433 / TOR M4 — maps <see cref="ReconciliationReport"/> to
/// <c>cnas.ReconciliationReports</c>. Enforces uniqueness on
/// <c>RunId</c> (one reconciliation per run) and pins the
/// <see cref="ReconciliationReport.ChecksumMatchRate"/> precision to
/// <c>(18, 4)</c>.
/// </summary>
public sealed class ReconciliationReportConfiguration : AuditableEntityConfiguration<ReconciliationReport>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ReconciliationReport> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReconciliationReports");

        builder.Property(e => e.RunId).IsRequired();
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.SourceRowCount).IsRequired();
        builder.Property(e => e.TargetRowCount).IsRequired();
        builder.Property(e => e.MissingInTargetCount).IsRequired();
        builder.Property(e => e.UnexpectedInTargetCount).IsRequired();
        builder.Property(e => e.ChecksumMatchRate)
            .IsRequired()
            .HasPrecision(18, 4);
        builder.Property(e => e.DiscrepancyDetailsJson).HasMaxLength(16384);
        builder.Property(e => e.ComputedAt).IsRequired();

        builder.HasIndex(e => e.RunId)
            .IsUnique()
            .HasDatabaseName("UX_ReconciliationReports_RunId");
    }
}
