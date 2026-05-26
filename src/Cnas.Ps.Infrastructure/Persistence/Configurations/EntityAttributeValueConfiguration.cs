using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2190-R2200 / TOR §15.6 FLEX 006 — maps <see cref="EntityAttributeValue"/>
/// to <c>cnas.EntityAttributeValues</c>. UNIQUE composite index over
/// (<c>EntityType</c>, <c>EntityId</c>, <c>AttributeCode</c>) so the
/// service layer's upsert path can rely on natural uniqueness rather than
/// hand-rolling a probe-then-insert race window.
/// </summary>
public sealed class EntityAttributeValueConfiguration : AuditableEntityConfiguration<EntityAttributeValue>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<EntityAttributeValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EntityAttributeValues");

        builder.Property(e => e.EntityType)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(e => e.EntityId).IsRequired();

        builder.Property(e => e.AttributeCode)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(e => e.Value)
            .IsRequired()
            .HasMaxLength(4096);

        builder.HasIndex(e => new { e.EntityType, e.EntityId, e.AttributeCode })
            .IsUnique()
            .HasDatabaseName("UX_EntityAttributeValues_Type_Id_Code");

        builder.HasIndex(e => new { e.EntityType, e.EntityId })
            .HasDatabaseName("IX_EntityAttributeValues_Type_Id");
    }
}
