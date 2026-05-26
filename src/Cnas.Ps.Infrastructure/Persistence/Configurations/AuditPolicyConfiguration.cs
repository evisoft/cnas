using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="AuditPolicy"/> to <c>cnas.AuditPolicies</c> — the admin-configurable
/// audit-policy registry consulted by the audit drainer at flush time (R0182 / SEC 042).
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexes.</b> The natural-key UNIQUE on <see cref="AuditPolicy.Code"/> guarantees no
/// two rows share a code; the migration's idempotent <c>ON CONFLICT (Code) DO NOTHING</c>
/// seed depends on this index. The composite <c>(Module, Screen, IsEnabled)</c> index
/// supports the resolver's per-event scan as the rule set grows: although the resolver
/// works off an in-memory snapshot (so the DB index isn't hit per audit-write), the
/// background refresh job does a full-table scan every 60 s — keeping the index keeps
/// that scan cheap.
/// </para>
/// <para>
/// <b>Column caps.</b> Mirrors the conventions used by neighbouring policy tables:
/// 80-char <see cref="AuditPolicy.Code"/>, 64-char <see cref="AuditPolicy.Module"/> /
/// <see cref="AuditPolicy.Screen"/>, 32-char <see cref="AuditPolicy.DataCategory"/>,
/// 256-char <see cref="AuditPolicy.EventCodePattern"/>, 512-char
/// <see cref="AuditPolicy.Description"/>.
/// </para>
/// <para>
/// <b>ExtraRedactKeys serialisation.</b> Stored as a JSONB column with a value
/// converter — Postgres-native list semantics, queryable from SQL if a future audit
/// explorer needs to filter by redact key. The InMemory test provider stores the
/// projection as the converted JSON text; round-trip equality is preserved through
/// the EF value comparer registered alongside the converter.
/// </para>
/// </remarks>
public sealed class AuditPolicyConfiguration : AuditableEntityConfiguration<AuditPolicy>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<AuditPolicy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditPolicies");

        builder.Property(p => p.Code).IsRequired().HasMaxLength(80);
        builder.Property(p => p.Module).IsRequired().HasMaxLength(64);
        builder.Property(p => p.Screen).IsRequired().HasMaxLength(64);
        builder.Property(p => p.DataCategory).HasMaxLength(32);
        builder.Property(p => p.EventCodePattern).IsRequired().HasMaxLength(256);
        builder.Property(p => p.OverrideSeverity);
        builder.Property(p => p.SuppressAudit).IsRequired();

        // ExtraRedactKeys persists as JSONB (Postgres) / JSON text (InMemory). The
        // value comparer ensures EF tracks list mutations correctly across the round
        // trip and lets idempotent updates (assigning the same list) avoid spurious
        // DbUpdateConcurrencyException on the xmin token.
        builder.Property(p => p.ExtraRedactKeys)
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v ?? new List<string>(), (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>>(
                    (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                    v => (v ?? new List<string>()).Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.OrdinalIgnoreCase))),
                    v => v.ToList()));

        builder.Property(p => p.Priority).IsRequired().HasDefaultValue(100);
        builder.Property(p => p.IsEnabled).IsRequired().HasDefaultValue(true);
        builder.Property(p => p.Description).HasMaxLength(512);

        // Natural-key UNIQUE on Code — see class remarks.
        builder.HasIndex(p => p.Code).IsUnique();

        // Composite index supporting the per-fire refresh scan + admin filters.
        builder.HasIndex(p => new { p.Module, p.Screen, p.IsEnabled });
    }
}
