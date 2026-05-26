using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Classifier"/> to <c>cnas.Classifiers</c>.</summary>
public sealed class ClassifierConfiguration : AuditableEntityConfiguration<Classifier>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Classifier> builder)
    {
        builder.ToTable("Classifiers");

        builder.Property(c => c.Kind).IsRequired().HasMaxLength(64);
        builder.Property(c => c.Code).IsRequired().HasMaxLength(64);
        builder.Property(c => c.LabelRo).IsRequired().HasMaxLength(256);
        builder.Property(c => c.LabelEn).HasMaxLength(256);
        builder.Property(c => c.LabelRu).HasMaxLength(256);
        builder.Property(c => c.ParentCode).HasMaxLength(64);
        builder.Property(c => c.Source).IsRequired().HasMaxLength(32);

        // R0401 / TOR CF 17.02-04 — read-only mirror flag. Defaults to false at
        // the database level so legacy internal rows behave unchanged after the
        // additive column lands.
        builder.Property(c => c.IsReadOnlyMirror).HasDefaultValue(false);

        builder.HasIndex(c => new { c.Kind, c.Code }).IsUnique();
        builder.HasIndex(c => new { c.Kind, c.ParentCode });
    }
}
