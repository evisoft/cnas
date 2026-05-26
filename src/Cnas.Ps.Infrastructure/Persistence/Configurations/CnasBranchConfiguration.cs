using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="CnasBranch"/> to <c>cnas.CnasBranches</c>. The natural key is
/// <see cref="CnasBranch.Code"/> (unique across active rows); the surrogate
/// <see cref="AuditableEntity.Id"/> stays internal and never crosses the API
/// boundary (the DTO surfaces <see cref="CnasBranch.Code"/> instead).
/// </summary>
public sealed class CnasBranchConfiguration : AuditableEntityConfiguration<CnasBranch>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<CnasBranch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CnasBranches");

        builder.Property(b => b.Code).IsRequired().HasMaxLength(64);
        builder.Property(b => b.Name).IsRequired().HasMaxLength(256);
        // R0027 / TOR ARH 022 — optional per-locale name columns. All three are
        // nullable so legacy rows behave unchanged; ILocalizedNameResolver
        // composes the fallback chain.
        builder.Property(b => b.NameRo).HasMaxLength(256);
        builder.Property(b => b.NameRu).HasMaxLength(256);
        builder.Property(b => b.NameEn).HasMaxLength(256);
        builder.Property(b => b.City).IsRequired().HasMaxLength(128);
        builder.Property(b => b.Address).HasMaxLength(512);
        builder.Property(b => b.Phone).HasMaxLength(32);
        builder.Property(b => b.OnlineSchedulingUrlTemplate).HasMaxLength(512);

        // Unique on Code — the deep-link contract pins the code as the public
        // identifier of a branch. Active and inactive rows share the namespace
        // because we never recycle a code; deactivation only hides a branch
        // from the public surface, it doesn't free up its code for reuse.
        builder.HasIndex(b => b.Code).IsUnique();
        builder.HasIndex(b => b.Name);
    }
}
