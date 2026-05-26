using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="SiemForwarderState"/> to <c>cnas.SiemForwarderState</c> — the
/// singleton-row checkpoint table backing the SIEM CEF / syslog forwarder
/// (R0190 / SEC 049).
/// </summary>
/// <remarks>
/// <para>
/// <b>Singular table name.</b> Unlike the rest of the schema (which pluralises by
/// convention), this table is intentionally named in the singular because the
/// "table" carries at most one row per environment — the singleton-by-known-key
/// pattern documented on <see cref="SiemForwarderState"/>. Pluralising would
/// misimply a collection semantics that does not exist.
/// </para>
/// <para>
/// <b>Indexes.</b> A UNIQUE index on <see cref="SiemForwarderState.Key"/> is the
/// DB-side safety net against a racing duplicate insert (two startup paths trying
/// to seed the row simultaneously). The standard <c>(IsActive)</c> and
/// <c>(CreatedAtUtc)</c> indexes contributed by
/// <see cref="AuditableEntityConfiguration{TEntity}"/> are also created so the
/// soft-delete-aware read path remains cheap (single-row but still index-served).
/// </para>
/// </remarks>
public sealed class SiemForwarderStateConfiguration : AuditableEntityConfiguration<SiemForwarderState>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SiemForwarderState> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SiemForwarderState");

        // Key cap leaves headroom for a future per-tenant variant (e.g. "tenant-NNN")
        // without touching the schema again.
        builder.Property(s => s.Key).IsRequired().HasMaxLength(32);

        builder.Property(s => s.LastForwardedAuditId).IsRequired();
        builder.Property(s => s.LastForwardedAtUtc);

        // The singleton-via-known-key invariant — only one row may carry Key="default".
        builder.HasIndex(s => s.Key).IsUnique();
    }
}
