using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — maps <see cref="ReportJob"/> to
/// <c>cnas.ReportJobs</c>. The table is the durable queue feeding the background
/// runner: the runner picks the oldest <c>Queued</c> row, flips it to
/// <c>Running</c>, runs the engine, and stamps a terminal status.
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexes.</b>
/// <list type="bullet">
///   <item><description>
///     <c>(Status, QueuedAtUtc)</c> — drives the runner's "oldest Queued"
///     pickup query. Ascending on the timestamp because the FIFO discipline
///     prefers the earliest-arrived job.
///   </description></item>
///   <item><description>
///     <c>(RequestedByUserId, QueuedAtUtc DESC)</c> — drives the per-user
///     history view (newest first).
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Column widths.</b>
/// <list type="bullet">
///   <item><description><c>Format</c> — <c>int</c> (stable enum ordinal of
///         <see cref="Cnas.Ps.Contracts.ExportFormat"/>).</description></item>
///   <item><description><c>Status</c> — <c>int</c> (stable enum ordinal of
///         <see cref="ReportJobStatus"/>).</description></item>
///   <item><description><c>FailureReason</c> — <c>varchar(2000)</c> nullable.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ReportJobConfiguration : AuditableEntityConfiguration<ReportJob>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ReportJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReportJobs");

        builder.Property(j => j.ReportTemplateId).IsRequired();
        builder.Property(j => j.RequestedByUserId).IsRequired();
        builder.Property(j => j.Format).IsRequired();
        builder.Property(j => j.Status)
            .HasConversion<int>()
            .IsRequired();
        builder.Property(j => j.QueuedAtUtc).IsRequired();
        builder.Property(j => j.StartedAtUtc);
        builder.Property(j => j.CompletedAtUtc);
        builder.Property(j => j.AttachmentRecordId);
        builder.Property(j => j.FailureReason).HasMaxLength(2000);
        builder.Property(j => j.DurationMs);

        // Runner pickup query — "oldest Queued first".
        builder.HasIndex(j => new { j.Status, j.QueuedAtUtc });

        // Per-user history view — newest first.
        builder.HasIndex(j => new { j.RequestedByUserId, j.QueuedAtUtc })
            .IsDescending(false, true);
    }
}
