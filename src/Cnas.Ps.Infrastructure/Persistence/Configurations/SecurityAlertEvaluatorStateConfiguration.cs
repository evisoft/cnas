using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="SecurityAlertEvaluatorState"/> to
/// <c>cnas.SecurityAlertEvaluatorState</c> — the singleton-row checkpoint table
/// backing the security-alert evaluator background job (R0189 / SEC 048).
/// </summary>
/// <remarks>
/// <para>
/// <b>Singular table name.</b> Like <c>SiemForwarderState</c> (R0190), the table is
/// intentionally named in the singular because it carries at most one row per
/// environment — the singleton-by-known-key pattern documented on
/// <see cref="SecurityAlertEvaluatorState"/>. Pluralising would misimply a collection
/// semantics that does not exist.
/// </para>
/// <para>
/// <b>Indexes.</b> A UNIQUE index on <see cref="SecurityAlertEvaluatorState.Key"/> is
/// the DB-side safety net against a racing duplicate insert (two startup paths trying
/// to seed the row simultaneously). The standard <c>(IsActive)</c> and
/// <c>(CreatedAtUtc)</c> indexes contributed by
/// <see cref="AuditableEntityConfiguration{TEntity}"/> are also created so the
/// soft-delete-aware read path remains cheap (single-row but still index-served).
/// </para>
/// </remarks>
public sealed class SecurityAlertEvaluatorStateConfiguration : AuditableEntityConfiguration<SecurityAlertEvaluatorState>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SecurityAlertEvaluatorState> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SecurityAlertEvaluatorState");

        // Key cap leaves headroom for a future per-tenant variant (e.g. "tenant-NNN")
        // without touching the schema again.
        builder.Property(s => s.Key).IsRequired().HasMaxLength(32);
        builder.Property(s => s.LastEvaluatedAuditId).IsRequired();

        // The singleton-via-known-key invariant — only one row may carry Key="default".
        builder.HasIndex(s => s.Key).IsUnique();
    }
}
