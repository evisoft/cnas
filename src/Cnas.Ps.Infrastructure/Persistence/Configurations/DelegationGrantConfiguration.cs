using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DelegationGrant"/> to <c>cnas.DelegationGrants</c> — the
/// authoritative store of time-bounded delegation grants (R0057 / SEC 026 / CF 16.11).
/// Domain-specific indexes (in addition to the soft-delete + audit-timestamp indexes
/// contributed by <see cref="AuditableEntityConfiguration{TEntity}"/>):
/// <list type="bullet">
///   <item><description><c>(GrantorUserId)</c> — "grants I issued" lookups.</description></item>
///   <item><description><c>(DelegateeUserId)</c> — "grants delegated to me" lookups.</description></item>
///   <item><description><c>(GrantorUserId, ValidFromUtc, ValidToUtc)</c> — active-at-T window probe.</description></item>
/// </list>
/// </summary>
public sealed class DelegationGrantConfiguration : AuditableEntityConfiguration<DelegationGrant>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<DelegationGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DelegationGrants");

        builder.Property(p => p.GrantorUserId).IsRequired();
        builder.Property(p => p.DelegateeUserId).IsRequired();
        builder.Property(p => p.ValidFromUtc).IsRequired();
        builder.Property(p => p.ValidToUtc).IsRequired();
        builder.Property(p => p.SuspendsGrantorRights).IsRequired();
        // Scope vocabulary stays free-form; 128 chars covers the dotted convention
        // (e.g. "approve.executory_documents.recovery") with comfortable headroom.
        builder.Property(p => p.Scope).IsRequired().HasMaxLength(128);
        builder.Property(p => p.GrantedAtUtc).IsRequired();
        builder.Property(p => p.RevokedAtUtc);
        // Mirrors the cap enforced by DelegationRevokeInputValidator (3..500 chars).
        builder.Property(p => p.RevokeReason).HasMaxLength(500);

        // Per-user lookups.
        builder.HasIndex(p => p.GrantorUserId);
        builder.HasIndex(p => p.DelegateeUserId);

        // Composite index covering the active-window probe used by ListActiveAsync.
        builder.HasIndex(p => new { p.GrantorUserId, p.ValidFromUtc, p.ValidToUtc });
    }
}
