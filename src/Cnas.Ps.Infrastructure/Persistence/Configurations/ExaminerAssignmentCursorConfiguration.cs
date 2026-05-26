using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ExaminerAssignmentCursor"/> to
/// <c>cnas.ExaminerAssignmentCursor</c> — the singleton-row checkpoint table
/// backing the round-robin examiner assignment service (R0570 / TOR CF 08.02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Singular table name.</b> Unlike the rest of the schema (which pluralises
/// by convention), this table is intentionally named in the singular because
/// the "table" carries at most one row per environment — the
/// singleton-by-known-key pattern documented on
/// <see cref="ExaminerAssignmentCursor"/>. Pluralising would misimply a
/// collection semantics that does not exist.
/// </para>
/// <para>
/// <b>Indexes.</b> A UNIQUE index on <see cref="ExaminerAssignmentCursor.Key"/>
/// is the DB-side safety net against a racing duplicate insert (two startup
/// paths trying to seed the row simultaneously).
/// </para>
/// </remarks>
public sealed class ExaminerAssignmentCursorConfiguration
    : AuditableEntityConfiguration<ExaminerAssignmentCursor>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ExaminerAssignmentCursor> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ExaminerAssignmentCursor");

        // Key cap leaves headroom for a future per-tenant variant
        // (e.g. "tenant-NNN") without touching the schema again.
        builder.Property(s => s.Key).IsRequired().HasMaxLength(32);
        builder.Property(s => s.NextIndex).IsRequired();

        // The singleton-via-known-key invariant — only one row may carry
        // Key="default" per environment.
        builder.HasIndex(s => s.Key).IsUnique();
    }
}
