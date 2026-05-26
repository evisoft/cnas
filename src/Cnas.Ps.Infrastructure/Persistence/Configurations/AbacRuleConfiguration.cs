using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2271 / TOR SEC 025 — EF Core configuration for <see cref="AbacRule"/>.
/// Maps the entity to <c>cnas.AbacRules</c> with a composite unique partial
/// index on <c>(RuleSetId, OrderIndex)</c> filtered to <c>IsActive=true</c>
/// so two active rules cannot share a slot. The partial filter keeps the
/// unique invariant out of the way of soft-deleted history rows.
/// </summary>
public sealed class AbacRuleConfiguration : AuditableEntityConfiguration<AbacRule>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<AbacRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AbacRules");

        builder.Property(p => p.RuleSetId).IsRequired();
        builder.Property(p => p.OrderIndex).IsRequired();

        builder.Property(p => p.Effect)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();

        builder.Property(p => p.ConditionExpression)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(p => p.Description).HasMaxLength(500);

        // Active-row natural-key partial unique index — two active rules in a
        // set may not share an OrderIndex. The HasFilter clause uses Postgres
        // quoting because the column name is PascalCase.
        builder.HasIndex(p => new { p.RuleSetId, p.OrderIndex })
            .IsUnique()
            .HasFilter("\"IsActive\" = true")
            .HasDatabaseName("UX_AbacRules_RuleSetId_OrderIndex_Active");

        // Evaluator scan index — fetches Active rules of a set in OrderIndex ASC.
        builder.HasIndex(p => new { p.RuleSetId, p.IsActive, p.OrderIndex })
            .HasDatabaseName("IX_AbacRules_RuleSetId_IsActive_OrderIndex");
    }
}
