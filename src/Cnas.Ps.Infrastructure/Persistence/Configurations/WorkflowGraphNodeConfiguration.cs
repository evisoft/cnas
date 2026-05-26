using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0123 / TOR CF 16.05 — maps <see cref="WorkflowGraphNode"/> to
/// <c>cnas.WorkflowGraphNodes</c>. Each row is one node in the persisted execution
/// graph of a specific <see cref="WorkflowDefinition"/> version. Natural key:
/// <c>(WorkflowDefinitionId, NodeCode)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Column caps.</b> <see cref="WorkflowGraphNode.NodeCode"/> is capped at 64 chars
/// to match the kebab/SCREAMING_SNAKE step-code convention used by R0126 step ACLs
/// (so a node code can be reused as an ACL step code without losing characters).
/// <see cref="WorkflowGraphNode.AssigneeRole"/> mirrors the 64-char role-code cap used
/// across the codebase. <see cref="WorkflowGraphNode.ConditionExpression"/> is capped at
/// 512 chars — long enough to express the kind of business-rule snippet the rule engine
/// understands while staying short of the JSONB-overhead threshold.
/// </para>
/// <para>
/// <b>Kind stored as int.</b> The <see cref="WorkflowNodeKind"/> enum is persisted via
/// <c>HasConversion&lt;int&gt;</c> so the numeric stability documented on the enum
/// applies at the column level. A future "add new kind" change appends an int value at
/// the end of the enum and the existing rows remain valid.
/// </para>
/// </remarks>
public sealed class WorkflowGraphNodeConfiguration : AuditableEntityConfiguration<WorkflowGraphNode>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<WorkflowGraphNode> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("WorkflowGraphNodes");

        builder.Property(n => n.WorkflowDefinitionId).IsRequired();
        builder.Property(n => n.NodeCode).IsRequired().HasMaxLength(64);
        builder.Property(n => n.Kind).IsRequired().HasConversion<int>();
        builder.Property(n => n.AssigneeRole).HasMaxLength(64);
        builder.Property(n => n.ConditionExpression).HasMaxLength(512);
        builder.Property(n => n.OrderIndex).IsRequired();

        // Natural key — at most one node with a given code within a workflow version.
        builder.HasIndex(n => new { n.WorkflowDefinitionId, n.NodeCode }).IsUnique();
    }
}
