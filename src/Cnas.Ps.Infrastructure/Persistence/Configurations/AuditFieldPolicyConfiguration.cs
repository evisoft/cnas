using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="AuditFieldPolicy"/> to <c>cnas.AuditFieldPolicies</c> — the
/// admin-configurable per-entity field-change policy consulted by the diff writer
/// before emitting an audit row (R0183 / SEC 043).
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexes.</b> The natural-key UNIQUE on <see cref="AuditFieldPolicy.EntityType"/>
/// guarantees one row per CLR type and powers the idempotent <c>ON CONFLICT (EntityType)
/// DO NOTHING</c> seed in the companion migration. A composite index on
/// <c>(EntityType, IsEnabled)</c> is unnecessary because the unique index already covers
/// the lookup pattern, but we keep <c>IsActive</c> indexed via the base configuration
/// (<see cref="AuditableEntityConfiguration{TEntity}"/>).
/// </para>
/// <para>
/// <b>Column caps.</b> 64-char <see cref="AuditFieldPolicy.EntityType"/> (CLR type
/// names are vastly smaller), 512-char <see cref="AuditFieldPolicy.Description"/>.
/// </para>
/// <para>
/// <b>TrackedFields / SuppressedFields serialisation.</b> Both lists are stored as
/// JSONB columns with a value converter — Postgres-native list semantics, queryable
/// from SQL if a future audit explorer needs to filter by tracked field. The InMemory
/// test provider stores the projection as the converted JSON text; round-trip
/// equality is preserved through the EF value comparer registered alongside the
/// converter. Mirrors the <see cref="AuditPolicy.ExtraRedactKeys"/> mapping pattern
/// from R0182 so seed migrations and admin tooling stay consistent.
/// </para>
/// </remarks>
public sealed class AuditFieldPolicyConfiguration : AuditableEntityConfiguration<AuditFieldPolicy>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<AuditFieldPolicy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditFieldPolicies");

        builder.Property(p => p.EntityType).IsRequired().HasMaxLength(64);
        builder.Property(p => p.RequireAnyChange).IsRequired().HasDefaultValue(true);
        builder.Property(p => p.Severity).IsRequired();
        builder.Property(p => p.IsEnabled).IsRequired().HasDefaultValue(true);
        builder.Property(p => p.Description).HasMaxLength(512);

        // TrackedFields persists as JSONB. Mirrors AuditPolicy.ExtraRedactKeys
        // pattern: value comparer ensures EF tracks list mutations correctly across
        // the round trip and lets idempotent updates (assigning the same list)
        // avoid spurious DbUpdateConcurrencyException on the xmin token.
        builder.Property(p => p.TrackedFields)
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v ?? new List<string>(), (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>>(
                    (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>(), StringComparer.Ordinal),
                    v => (v ?? new List<string>()).Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
                    v => v.ToList()));

        builder.Property(p => p.SuppressedFields)
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v ?? new List<string>(), (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<string>>(
                    (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>(), StringComparer.Ordinal),
                    v => (v ?? new List<string>()).Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
                    v => v.ToList()));

        // Natural-key UNIQUE on EntityType — see class remarks.
        builder.HasIndex(p => p.EntityType).IsUnique();
    }
}
