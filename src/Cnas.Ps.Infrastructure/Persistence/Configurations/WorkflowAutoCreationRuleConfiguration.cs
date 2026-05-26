using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0540 / TOR CF 05.01 (iter 134) — EF Core configuration for
/// <see cref="WorkflowAutoCreationRule"/>. Maps the entity to
/// <c>cnas.WorkflowAutoCreationRules</c> with a partial unique index on
/// (<see cref="WorkflowAutoCreationRule.FromStatus"/>,
///  <see cref="WorkflowAutoCreationRule.ToStatus"/>,
///  <see cref="WorkflowAutoCreationRule.TaskKind"/>) filtered to
/// <c>IsActive = true</c> so two active rules cannot share a slot. The partial
/// filter keeps the uniqueness invariant out of the way of soft-deleted history
/// rows.
/// </summary>
public sealed class WorkflowAutoCreationRuleConfiguration
    : AuditableEntityConfiguration<WorkflowAutoCreationRule>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<WorkflowAutoCreationRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("WorkflowAutoCreationRules");

        builder.Property(r => r.FromStatus).IsRequired().HasConversion<int>();
        builder.Property(r => r.ToStatus).IsRequired().HasConversion<int>();
        builder.Property(r => r.TaskKind).IsRequired().HasMaxLength(64);
        builder.Property(r => r.AssigneeRole).IsRequired().HasMaxLength(64);
        builder.Property(r => r.DueWithinDays).IsRequired();

        // Active-row natural-key partial unique index — two active rules
        // describing the same transition + task kind would be ambiguous to the
        // auto-creator. The HasFilter clause uses Postgres quoting because the
        // column name is PascalCase.
        builder.HasIndex(r => new { r.FromStatus, r.ToStatus, r.TaskKind })
            .IsUnique()
            .HasFilter("\"IsActive\" = true")
            .HasDatabaseName("UX_WorkflowAutoCreationRules_From_To_Kind_Active");

        // Auto-creator scan index — fetches Active rules for a (From, To)
        // pair on every status transition. The unique index above is partial
        // on IsActive=true, so the scan would already use it; this redundant
        // narrower index speeds up the predicate when the table grows.
        builder.HasIndex(r => new { r.FromStatus, r.ToStatus, r.IsActive })
            .HasDatabaseName("IX_WorkflowAutoCreationRules_From_To_Active");
    }
}
