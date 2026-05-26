using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1906 / TOR Annex 6 — maps <see cref="ReportDistributionDispatch"/> to
/// <c>cnas.ReportDistributionDispatches</c>. Indexes back the three
/// dashboard projections: per-run drill-down, status × dispatched-at, and
/// per-rule history.
/// </summary>
public sealed class ReportDistributionDispatchConfiguration : AuditableEntityConfiguration<ReportDistributionDispatch>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ReportDistributionDispatch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReportDistributionDispatches");

        builder.Property(e => e.RuleId).IsRequired();
        builder.Property(e => e.ReportRunSqid).IsRequired().HasMaxLength(64);

        builder.Property(e => e.Channel)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.RecipientKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.RecipientCode)
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(e => e.DispatchedAt).IsRequired();
        builder.Property(e => e.DeliveredAt);
        builder.Property(e => e.FailureReason).HasMaxLength(500);
        builder.Property(e => e.RetryCount).IsRequired();

        builder.HasOne<ReportDistributionRule>()
            .WithMany()
            .HasForeignKey(e => e.RuleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.ReportRunSqid)
            .HasDatabaseName("IX_ReportDistributionDispatches_ReportRunSqid");

        builder.HasIndex(e => new { e.Status, e.DispatchedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_ReportDistributionDispatches_Status_DispatchedAt");

        builder.HasIndex(e => e.RuleId)
            .HasDatabaseName("IX_ReportDistributionDispatches_RuleId");
    }
}
