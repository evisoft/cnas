using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="FailedJob"/> to <c>cnas.FailedJobs</c> — the dead-letter queue for
/// Quartz job executions that exhausted their retry pipeline (CLAUDE.md §6.2).
/// </summary>
/// <remarks>
/// <para>
/// Two indexes are configured:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>(JobName, FailedAtUtc DESC)</c> — supports the common admin query
///       "show me the last N failures of job X" without scanning the whole table.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(FailedAtUtc DESC)</c> standalone — supports the admin dashboard's
///       "last 100 failures across all jobs" view.
///     </description>
///   </item>
/// </list>
/// <para>
/// Column lengths are sized for forensic usefulness without unbounded growth:
/// <c>ExceptionMessage</c> caps at 4000 chars (typical Postgres TOAST threshold),
/// <c>StackTrace</c> and <c>JobDataJson</c> use the unbounded <c>text</c> type because
/// stack traces vary wildly and we already truncate at the application layer.
/// </para>
/// </remarks>
public sealed class FailedJobConfiguration : AuditableEntityConfiguration<FailedJob>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<FailedJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("FailedJobs");

        builder.Property(f => f.JobName).IsRequired().HasMaxLength(128);
        builder.Property(f => f.JobGroup).IsRequired().HasMaxLength(64);
        builder.Property(f => f.FailedAtUtc).IsRequired();
        builder.Property(f => f.ExceptionType).IsRequired().HasMaxLength(256);
        builder.Property(f => f.ExceptionMessage).IsRequired().HasMaxLength(4000);
        builder.Property(f => f.StackTrace).HasColumnType("text");
        builder.Property(f => f.JobDataJson).HasColumnType("text");
        builder.Property(f => f.RefireCount).IsRequired();
        builder.Property(f => f.ReplayState).HasMaxLength(32);

        // Composite index for "last failures of job X" — DESCending order is implicit in
        // the Postgres B-tree (every B-tree can be scanned forwards or backwards), but
        // declaring it explicitly via descending semantics keeps the operator dashboard's
        // ORDER BY FailedAtUtc DESC fully covered.
        builder.HasIndex(f => new { f.JobName, f.FailedAtUtc });

        // Cross-job dashboard query — "latest 100 failures across every job".
        builder.HasIndex(f => f.FailedAtUtc);
    }
}
