using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0123 / TOR CF 16.05 — maps <see cref="WorkflowGraphEdge"/> to
/// <c>cnas.WorkflowGraphEdges</c>. Each row is one directed edge of the persisted
/// execution graph; together with <see cref="WorkflowGraphNode"/> rows it forms the
/// substrate consumed by the workflow graph executor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composite indexes.</b> Two non-unique B-tree indexes back the executor's hot
/// queries: <c>(WorkflowDefinitionId, SourceNodeId)</c> for the "what comes next"
/// outbound lookup and <c>(WorkflowDefinitionId, TargetNodeId)</c> for the
/// AND-join "how many incoming edges" check. Both are non-unique because a node may
/// have multiple outgoing (AND-split / OR-split) or incoming (AND-join / OR-join)
/// edges.
/// </para>
/// <para>
/// <b>Label cap.</b> 64 chars matches the rule-engine verdict-label vocabulary used
/// by R0124 (verdict labels are stable SCREAMING_SNAKE/kebab strings).
/// </para>
/// </remarks>
public sealed class WorkflowGraphEdgeConfiguration : AuditableEntityConfiguration<WorkflowGraphEdge>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<WorkflowGraphEdge> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("WorkflowGraphEdges");

        builder.Property(e => e.WorkflowDefinitionId).IsRequired();
        builder.Property(e => e.SourceNodeId).IsRequired();
        builder.Property(e => e.TargetNodeId).IsRequired();
        builder.Property(e => e.Label).HasMaxLength(64);
        builder.Property(e => e.OrderIndex).IsRequired();

        // Executor outbound lookup — "give me every edge whose source is X within W".
        builder.HasIndex(e => new { e.WorkflowDefinitionId, e.SourceNodeId });

        // AND-join inbound count — "give me every edge whose target is X within W".
        builder.HasIndex(e => new { e.WorkflowDefinitionId, e.TargetNodeId });
    }
}
