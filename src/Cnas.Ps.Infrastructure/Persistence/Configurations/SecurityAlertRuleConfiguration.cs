using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="SecurityAlertRule"/> to <c>cnas.SecurityAlertRules</c> — the
/// persisted rule set evaluated by <c>SecurityAlertEvaluatorJob</c> (R0189 / SEC 048).
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexes.</b> The natural-key UNIQUE on <see cref="SecurityAlertRule.Code"/> is the
/// DB-side safety net against two seed migrations racing to insert the same code (the
/// idempotent ON CONFLICT seed in the migration handles the happy path; the index is the
/// belt-and-braces guard). A composite <c>(IsActive, Code)</c> index supports the
/// evaluator's per-fire <c>Where(r =&gt; r.IsActive)</c> scan — rules are few and the
/// scan is cheap, but indexing keeps the per-iteration plan stable as the rule set
/// grows. Standard <c>(IsActive)</c> and <c>(CreatedAtUtc)</c> indexes are inherited
/// from <see cref="AuditableEntityConfiguration{TEntity}"/>.
/// </para>
/// <para>
/// <b>Column caps.</b> <see cref="SecurityAlertRule.Code"/> and
/// <see cref="SecurityAlertRule.RecipientGroup"/> are capped at 64 characters to align
/// with the role-code / event-code conventions used elsewhere in the schema.
/// <see cref="SecurityAlertRule.EventCodePattern"/> widens to 256 characters because
/// regex literals can grow (alternation, character classes) — 256 is comfortable for
/// every reasonable pattern.
/// </para>
/// </remarks>
public sealed class SecurityAlertRuleConfiguration : AuditableEntityConfiguration<SecurityAlertRule>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SecurityAlertRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SecurityAlertRules");

        builder.Property(r => r.Code).IsRequired().HasMaxLength(64);
        builder.Property(r => r.EventCodePattern).IsRequired().HasMaxLength(256);
        builder.Property(r => r.WindowSeconds).IsRequired();
        builder.Property(r => r.ThresholdCount).IsRequired();
        builder.Property(r => r.AlertSeverity).IsRequired();
        builder.Property(r => r.RecipientGroup).IsRequired().HasMaxLength(64);
        builder.Property(r => r.CooldownSeconds).IsRequired();
        builder.Property(r => r.LastFiredAtUtc);

        // Natural-key UNIQUE on Code — operators reference rules by code, and the
        // ON CONFLICT seed in the migration depends on this index existing.
        builder.HasIndex(r => r.Code).IsUnique();

        // Composite index supports the evaluator's IsActive scan as the rule set grows.
        // Today's rule count is ≤4 so the scan is trivially cheap, but the index keeps
        // the plan stable if operators add 50-100 rules over time.
        builder.HasIndex(r => new { r.IsActive, r.Code });
    }
}
