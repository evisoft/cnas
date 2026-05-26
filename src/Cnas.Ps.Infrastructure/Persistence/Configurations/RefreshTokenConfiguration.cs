using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="RefreshToken"/> to <c>cnas.RefreshTokens</c> — the opaque-token store
/// behind the R0053 JWT-access + refresh-token pipeline (CLAUDE.md §5.3 / SEC 018).
/// </summary>
/// <remarks>
/// <para>
/// Three indexes are configured (alongside the two contributed by
/// <see cref="AuditableEntityConfiguration{TEntity}"/> — <c>IsActive</c> and
/// <c>CreatedAtUtc</c>):
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE(TokenHash)</c> — every refresh-token hash is unique. The service
///       layer looks rows up by hash, so this index doubles as both a lookup helper
///       and a defensive guarantee that a hash collision (or an EF mis-write) cannot
///       silently produce two rows that decrypt to the same plaintext.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(FamilyId, ConsumedAtUtc)</c> — supports the family-revoke query
///       ("set RevokedAtUtc on every live row sharing this family id") plus the
///       reuse-detection guard's "find live tokens in this family" probe.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(UserId)</c> — supports per-user session listings ("show me the user's
///       active refresh tokens"); admin surfaces and forensic dashboards.
///     </description>
///   </item>
/// </list>
/// <para>
/// Column lengths are sized for the canonical wire shape: <see cref="RefreshToken.TokenHash"/>
/// is a SHA-256 hex digest (exactly 64 chars); <see cref="RefreshToken.RevokedReason"/> caps
/// at 64 chars to discourage drift toward free-form prose (the field is meant for stable
/// machine-readable reasons such as <c>"logout"</c>, not user-facing messages).
/// </para>
/// </remarks>
public sealed class RefreshTokenConfiguration : AuditableEntityConfiguration<RefreshToken>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RefreshTokens");

        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(t => t.FamilyId).IsRequired();
        builder.Property(t => t.UserId).IsRequired();
        builder.Property(t => t.IssuedAtUtc).IsRequired();
        builder.Property(t => t.ExpiresAtUtc).IsRequired();
        builder.Property(t => t.RevokedReason).HasMaxLength(64);

        // UNIQUE — every issued refresh-token hash is unique.
        builder.HasIndex(t => t.TokenHash).IsUnique();

        // Family revoke / live-token-in-family probes.
        builder.HasIndex(t => new { t.FamilyId, t.ConsumedAtUtc });

        // Per-user session listings.
        builder.HasIndex(t => t.UserId);
    }
}
