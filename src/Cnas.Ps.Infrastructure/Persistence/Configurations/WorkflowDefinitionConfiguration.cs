using System.Collections.Generic;
using System.Text.Json;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="WorkflowDefinition"/> to <c>cnas.WorkflowDefinitions</c> — the
/// append-only repository of versioned BPMN / workflow-graph JSON payloads consumed by
/// UC16 (configurez flux de lucru). Two domain-specific indexes are declared in addition
/// to the soft-delete + audit indexes contributed by
/// <see cref="AuditableEntityConfiguration{TEntity}"/>:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE (Code, Version)</c> — natural key. Two rows with the same code and
///       version are a programming error in the service layer; the unique constraint is
///       the database-level safety net.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(Code, IsCurrent) WHERE IsCurrent = true</c> — partial index supporting the
///       canonical lookup performed by <c>GetDefinitionAsync</c>. Filtering on
///       <c>IsCurrent = true</c> keeps the index slim regardless of how many historical
///       revisions accumulate over time.
///     </description>
///   </item>
/// </list>
/// <para>
/// <c>DefinitionJson</c> is mapped as PostgreSQL <c>text</c> (not <c>jsonb</c>) because
/// the workflow JSON is round-tripped verbatim and never queried by JSON-path expressions
/// at this layer. The service performs structural validation via <c>JsonDocument.Parse</c>
/// before persistence.
/// </para>
/// </remarks>
public sealed class WorkflowDefinitionConfiguration : AuditableEntityConfiguration<WorkflowDefinition>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<WorkflowDefinition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("WorkflowDefinitions");

        // Workflow codes are administrator-chosen identifiers — the 64-character cap is
        // generous (the longest production code today is "WF-DISABILITY-REASSESSMENT" at
        // 24 chars) and matches the cap used on ServicePassport.WorkflowCode so the two
        // columns can hold mutually-compatible values.
        builder.Property(w => w.Code).IsRequired().HasMaxLength(64);

        builder.Property(w => w.Version).IsRequired();

        // text rather than jsonb — see class-level remarks for the rationale.
        builder.Property(w => w.DefinitionJson)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(w => w.IsCurrent).IsRequired();

        // R0129 / CF 15.04 — chain pointers maintained by VersionedDefinitionWriter.
        // Self-FK lookups (e.g. "give me the row that superseded this one") use the
        // standard B-tree index implicitly via the FK target id.
        builder.Property(w => w.SupersededByDefinitionId);
        builder.Property(w => w.SupersededAtUtc);
        builder.Property(w => w.SupersedesDefinitionId);

        // R0124 / CF 16.08 — decision-engine rule-pack codes keyed at lifecycle hooks.
        // Capped at 80 chars: the IRulePackEvaluator key vocabulary matches the
        // service-passport / classifier code cap conventions.
        builder.Property(w => w.StartRulePackCode).HasMaxLength(80);
        builder.Property(w => w.TransitionRulePackCode).HasMaxLength(80);
        builder.Property(w => w.CompletionRulePackCode).HasMaxLength(80);

        // R0126 / CF 16.10 — workflow-scoped ACL. AllowedRoles / AllowedGroups persist
        // as JSONB list of strings (mirrors AuditPolicy.ExtraRedactKeys). The value
        // comparer keeps EF's change tracking precise on list mutations so idempotent
        // updates don't trigger spurious xmin concurrency conflicts.
        builder.Property(w => w.AllowedRoles)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new List<string>(), (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>(), StringComparer.Ordinal),
                    v => (v ?? new List<string>()).Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
                    v => v.ToList()));

        builder.Property(w => w.AllowedGroups)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new List<string>(), (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (a, b) => (a ?? new List<string>()).SequenceEqual(b ?? new List<string>(), StringComparer.Ordinal),
                    v => (v ?? new List<string>()).Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode(StringComparison.Ordinal))),
                    v => v.ToList()));

        // Natural key. Inserting a duplicate (Code, Version) is a programming error in the
        // service — the unique constraint converts it into a deterministic DbUpdateException.
        builder.HasIndex(w => new { w.Code, w.Version }).IsUnique();

        // Partial index supporting GetDefinitionAsync — the predicate restricts the
        // index to the single "current" row per code, which is exactly the query shape.
        // The HasFilter clause uses Postgres quoting because the column name is PascalCase.
        builder.HasIndex(w => new { w.Code, w.IsCurrent })
            .HasFilter("\"IsCurrent\" = true");

        // R0671 / CF 18.06 — workflow category code grouping workflows by domain
        // (e.g. "pension", "indemnization"). 64 chars matches WorkflowDefinition.Code;
        // no dedicated index — IAccessScopeFilter joins via the WorkflowTask → parent
        // definition row, so filtering happens on the already-indexed Id and the
        // CategoryCode is materialised on the projection.
        builder.Property(w => w.CategoryCode).HasMaxLength(64);
    }
}
