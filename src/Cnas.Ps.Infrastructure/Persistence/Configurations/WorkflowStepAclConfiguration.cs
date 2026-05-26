using System.Collections.Generic;
using System.Text.Json;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="WorkflowStepAcl"/> to <c>cnas.WorkflowStepAcls</c> — the per-workflow
/// per-step ACL refinement consulted by <c>IWorkflowAclService.CanHandleAsync</c> on
/// every workflow-task mutation entry point (R0126 / CF 16.10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural key.</b> Composite UNIQUE on (<see cref="WorkflowStepAcl.WorkflowDefinitionId"/>,
/// <see cref="WorkflowStepAcl.StepCode"/>) — at most one ACL row per (workflow, step).
/// The CRUD service's upsert picks the existing row off this index; the DB-level
/// uniqueness is the safety net.
/// </para>
/// <para>
/// <b>RequiredRoles / RequiredGroups serialisation.</b> Stored as <c>jsonb</c> with a
/// value converter mirroring <see cref="AuditPolicyConfiguration"/>. The
/// <see cref="ValueComparer{T}"/> compares element-by-element ordinally so EF Core
/// tracks list mutations correctly across the round trip and idempotent updates
/// (assigning the same list) avoid spurious <c>DbUpdateConcurrencyException</c> on
/// the xmin token.
/// </para>
/// <para>
/// <b>Column caps.</b> 64-char <see cref="WorkflowStepAcl.StepCode"/> matches the BPMN
/// activity-id length seen in production workflows; 128-char
/// <see cref="WorkflowStepAcl.RequiredPermission"/> matches the permission-vocabulary
/// length cap; 512-char <see cref="WorkflowStepAcl.Description"/> mirrors neighbouring
/// policy tables.
/// </para>
/// </remarks>
public sealed class WorkflowStepAclConfiguration : AuditableEntityConfiguration<WorkflowStepAcl>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<WorkflowStepAcl> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("WorkflowStepAcls");

        builder.Property(a => a.WorkflowDefinitionId).IsRequired();
        builder.Property(a => a.StepCode).IsRequired().HasMaxLength(64);
        builder.Property(a => a.RequiredPermission).HasMaxLength(128);
        builder.Property(a => a.Description).HasMaxLength(512);

        // RequiredRoles persists as JSONB list of strings. Mirrors the AuditPolicy
        // ExtraRedactKeys converter — JsonSerializer round-trips and a ValueComparer
        // keeps EF's change tracking precise on list mutations.
        builder.Property(a => a.RequiredRoles)
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

        // RequiredGroups — identical JSONB list-of-strings shape as RequiredRoles. The
        // duplication is intentional: each column carries its own value comparer and
        // is independently mutable, so a shared converter helper would obscure the
        // EF mapping without saving meaningful code.
        builder.Property(a => a.RequiredGroups)
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

        // Composite natural-key UNIQUE: one ACL per (workflow, step).
        builder.HasIndex(a => new { a.WorkflowDefinitionId, a.StepCode }).IsUnique();
    }
}
