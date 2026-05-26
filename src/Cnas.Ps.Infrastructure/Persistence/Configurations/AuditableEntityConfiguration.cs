using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Shared base configuration for every entity that inherits <see cref="AuditableEntity"/>.
/// Applies ARH naming (PascalCase columns), index on IsActive for soft-delete filtering,
/// and indexes on the audit timestamps.
/// </summary>
public abstract class AuditableEntityConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : AuditableEntity
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.CreatedAtUtc).IsRequired();
        builder.Property(e => e.CreatedBy).HasMaxLength(64);
        builder.Property(e => e.UpdatedBy).HasMaxLength(64);
        builder.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);

        builder.HasIndex(e => e.IsActive);
        builder.HasIndex(e => e.CreatedAtUtc);

        ConfigureEntity(builder);
    }

    /// <summary>Hook for the concrete configuration to map domain-specific columns/indexes.</summary>
    protected abstract void ConfigureEntity(EntityTypeBuilder<TEntity> builder);
}
