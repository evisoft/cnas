using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ReportRun"/> to <c>cnas.ReportRuns</c>. The table is the append-only
/// execution history for R0156 ad-hoc report runs. One row per
/// <c>IReportEngine.RunAsync</c> / <c>ExportAsync</c> call.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>
///     <c>(ReportTemplateId, ExecutedAtUtc DESC)</c> — supports the "show me the most
///     recent runs of this template" query. Descending on the timestamp keeps the index
///     aligned with the natural read pattern.
///   </description></item>
///   <item><description>
///     <c>(ExecutedByUserId)</c> — supports per-user activity-feed queries.
///   </description></item>
/// </list>
/// <para>
/// <b>Column widths and types.</b>
/// <list type="bullet">
///   <item><description><c>OutcomeCode</c> — <c>varchar(32)</c> (one of the stable strings on <see cref="ReportRun.OutcomeCode"/>).</description></item>
///   <item><description><c>FailureReason</c> — <c>varchar(512)</c> (nullable; capped to prevent runaway error messages from bloating the table).</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ReportRunConfiguration : AuditableEntityConfiguration<ReportRun>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ReportRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReportRuns");

        builder.Property(r => r.ReportTemplateId).IsRequired();
        builder.Property(r => r.ExecutedByUserId).IsRequired();
        builder.Property(r => r.ExecutedAtUtc).IsRequired();
        builder.Property(r => r.RowCount).IsRequired();
        builder.Property(r => r.OutcomeCode).IsRequired().HasMaxLength(32);
        builder.Property(r => r.DurationMs).IsRequired();
        builder.Property(r => r.FailureReason).HasMaxLength(512);

        // Most recent first per template — the natural read pattern for the run
        // history pane and forensics queries. The descending hint matches the EF
        // Core IsDescending() shape supported on Postgres.
        builder.HasIndex(r => new { r.ReportTemplateId, r.ExecutedAtUtc })
            .IsDescending(false, true);

        // Per-user activity feeds.
        builder.HasIndex(r => r.ExecutedByUserId);
    }
}
