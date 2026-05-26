using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2271 / TOR SEC 025 — EF Core configuration for <see cref="AbacRuleSet"/>.
/// Maps the entity to <c>cnas.AbacRuleSets</c> with a unique index on
/// <see cref="AbacRuleSet.PolicyName"/> so duplicate registrations are
/// rejected at the database level (the service-layer guard returns
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.AbacDuplicatePolicyName"/>
/// before reaching the database, but the unique index is defence-in-depth).
/// </summary>
/// <remarks>
/// <para>
/// Enums persist as stable name strings so re-ordering the underlying
/// integer values cannot silently flip an Allow rule set to Deny.
/// </para>
/// </remarks>
public sealed class AbacRuleSetConfiguration : AuditableEntityConfiguration<AbacRuleSet>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<AbacRuleSet> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AbacRuleSets");

        builder.Property(p => p.PolicyName).IsRequired().HasMaxLength(64);
        builder.Property(p => p.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(p => p.Description).HasMaxLength(1000);

        builder.Property(p => p.DefaultEffect)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();

        builder.Property(p => p.RegisteredByUserId).IsRequired();

        // Unique stable policy name per system.
        builder.HasIndex(p => p.PolicyName)
            .IsUnique()
            .HasDatabaseName("UX_AbacRuleSets_PolicyName");

        // Listing helper — admin dashboards filter by IsActive + recent first.
        builder.HasIndex(p => new { p.IsActive, p.PolicyName })
            .HasDatabaseName("IX_AbacRuleSets_IsActive_PolicyName");

        builder.HasMany(p => p.Rules)
            .WithOne(r => r.RuleSet!)
            .HasForeignKey(r => r.RuleSetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
