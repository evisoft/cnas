using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Report"/> to <c>cnas.Reports</c>.</summary>
public sealed class ReportConfiguration : AuditableEntityConfiguration<Report>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Report> builder)
    {
        builder.ToTable("Reports");

        builder.Property(r => r.Code).IsRequired().HasMaxLength(64);
        builder.Property(r => r.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(r => r.QueryTemplate).IsRequired();
        builder.Property(r => r.ParameterSchemaJson).HasColumnType("jsonb");
        builder.Property(r => r.DefaultFormat).IsRequired().HasMaxLength(8);

        // R1900-R1905 / iter-145 — catalog metadata block.
        // String columns default to empty (existing rows pre-iter-145 carry NULL → empty after seeder run).
        builder.Property(r => r.NameRo).IsRequired().HasMaxLength(256).HasDefaultValue(string.Empty);
        builder.Property(r => r.Purpose).IsRequired().HasMaxLength(1024).HasDefaultValue(string.Empty);
        builder.Property(r => r.Audience).IsRequired().HasMaxLength(128).HasDefaultValue(string.Empty);
        builder.Property(r => r.Frequency).IsRequired().HasMaxLength(32).HasDefaultValue("OnDemand");
        builder.Property(r => r.ColumnsJson).HasColumnType("jsonb").HasDefaultValue("[]");
        builder.Property(r => r.RbacRole).IsRequired().HasMaxLength(64).HasDefaultValue("cnas-admin");
        builder.Property(r => r.Schedule).IsRequired().HasMaxLength(128).HasDefaultValue("OnDemand");
        builder.Property(r => r.OutputFormatsJson).HasColumnType("jsonb").HasDefaultValue("[\"csv\"]");
        builder.Property(r => r.Category).IsRequired().HasMaxLength(64).HasDefaultValue("Statistical");

        builder.HasIndex(r => r.Code).IsUnique();
        builder.HasIndex(r => r.IsPublic);
        builder.HasIndex(r => r.Category);
        builder.HasIndex(r => r.Frequency);
    }
}
