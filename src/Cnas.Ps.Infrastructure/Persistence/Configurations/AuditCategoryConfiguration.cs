using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0196 / TOR CF 23.02 — maps <see cref="AuditCategory"/> to
/// <c>cnas.AuditCategories</c>. Enforces a unique <c>Code</c> and an index
/// on <c>IsActive</c> for the operator "list active categories" path.
/// </summary>
public sealed class AuditCategoryConfiguration : AuditableEntityConfiguration<AuditCategory>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<AuditCategory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditCategories");

        builder.Property(e => e.Code).IsRequired().HasMaxLength(64);
        builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(256);
        // R0027 / TOR ARH 022 — optional per-locale name columns. Nullable so the
        // resolver falls back to DisplayName when not curated.
        builder.Property(e => e.NameRo).HasMaxLength(256);
        builder.Property(e => e.NameRu).HasMaxLength(256);
        builder.Property(e => e.NameEn).HasMaxLength(256);
        builder.Property(e => e.Description).HasMaxLength(1000);
        builder.Property(e => e.DefaultSeverity)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.HasIndex(e => e.Code)
            .IsUnique()
            .HasDatabaseName("UX_AuditCategories_Code");

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_AuditCategories_IsActive");
    }
}
