using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="WorkflowTask"/> to <c>cnas.WorkflowTasks</c>.</summary>
public sealed class WorkflowTaskConfiguration : AuditableEntityConfiguration<WorkflowTask>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<WorkflowTask> builder)
    {
        builder.ToTable("WorkflowTasks");

        builder.Property(t => t.Title).IsRequired().HasMaxLength(256);
        builder.Property(t => t.Status).IsRequired().HasConversion<int>();
        builder.Property(t => t.GroupCode).HasMaxLength(64);

        // R0123 / CF 16.05 — graph-anchor columns added by the workflow graph executor.
        // NodeCode is capped at 64 chars to match the WorkflowGraphNode.NodeCode cap so
        // a task can always carry the same code it was anchored to. ParentSplitTaskId is
        // a self-reference into WorkflowTasks but EF treats it as a plain bigint column
        // (no FK constraint declared) because the AND-join lookup is a sargable filter,
        // not a navigation property — keeping the FK off avoids cycles in the model.
        builder.Property(t => t.NodeCode).HasMaxLength(64);
        builder.Property(t => t.ParentSplitTaskId);

        // R0127 / CF 16.11 — reassignment fields. ReassignmentReason is capped at 500
        // characters to mirror the validator. ReassignmentCount defaults to 0 — the
        // database default keeps existing rows valid after the migration without a
        // back-fill UPDATE.
        builder.Property(t => t.ReassignmentReason).HasMaxLength(500);
        builder.Property(t => t.ReassignmentCount).IsRequired().HasDefaultValue(0);

        builder.HasIndex(t => new { t.AssignedUserId, t.Status });
        builder.HasIndex(t => new { t.GroupCode, t.Status });
        builder.HasIndex(t => t.DueAtUtc);

        // R0202 / CF 20.05 — the unclaimed-task escalation job filters on this column. The
        // partial-friendly nullable index keeps the SLA sweep sargable even at full
        // production volume (rows where the column is null are excluded from the index).
        builder.HasIndex(t => t.UnclaimedSinceUtc);

        // R0127 / CF 16.11 — the absence-revert sweep filters on DelegatedFromAbsenceId
        // when completing an absence. A nullable index keeps the revert query sargable
        // without bloating the table for rows that have never been delegated.
        builder.HasIndex(t => t.DelegatedFromAbsenceId);

        // R0123 / CF 16.05 — AND-join sibling lookup. The executor counts completed
        // siblings under a given AND-split anchor via this column. Nullable, so the
        // index is partial-friendly and only carries the rows that actually belong to
        // a fan-out group.
        builder.HasIndex(t => t.ParentSplitTaskId);
    }
}
