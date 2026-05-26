using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0125 / CF 16.09 — maps <see cref="WorkflowTaskStepHistory"/> to
/// <c>cnas.WorkflowTaskStepHistories</c>. The projection is append-only and indexed
/// by <c>(WorkflowTaskId, OccurredAt)</c> so per-task chronological reads are O(N)
/// (server-side ORDER BY consumes the index directly), and by
/// <c>(EventKind, OccurredAt DESC)</c> so cross-task dashboards (e.g. "recent SLA
/// breaches") can be served without a full scan.
/// </summary>
public sealed class WorkflowTaskStepHistoryConfiguration
    : AuditableEntityConfiguration<WorkflowTaskStepHistory>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<WorkflowTaskStepHistory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("WorkflowTaskStepHistories");

        builder.Property(h => h.WorkflowTaskId).IsRequired();
        builder.Property(h => h.StepCode).IsRequired().HasMaxLength(64);
        builder.Property(h => h.EventKind)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(h => h.OccurredAt).IsRequired();
        builder.Property(h => h.ActorUserId);
        builder.Property(h => h.DecisionCode).HasMaxLength(64);
        builder.Property(h => h.Note).HasMaxLength(1000);

        builder.HasIndex(h => new { h.WorkflowTaskId, h.OccurredAt });
        builder.HasIndex(h => new { h.EventKind, h.OccurredAt })
            .HasDatabaseName("IX_WorkflowTaskStepHistories_EventKind_OccurredAt");
    }
}
