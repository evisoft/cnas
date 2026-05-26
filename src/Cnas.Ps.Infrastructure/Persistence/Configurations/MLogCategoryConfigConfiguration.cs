using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0116 + R0195 / TOR SEC 054-055 — maps <see cref="MLogCategoryConfig"/> to
/// <c>cnas.MLogCategoryConfigs</c>. Enforces a unique <c>CategoryCode</c> and
/// indexes the active subset for the audit drainer's lookup hot path.
/// </summary>
public sealed class MLogCategoryConfigConfiguration
    : AuditableEntityConfiguration<MLogCategoryConfig>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MLogCategoryConfig> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MLogCategoryConfigs");

        builder.Property(e => e.CategoryCode)
            .IsRequired()
            .HasMaxLength(MLogCategoryConfig.MaxCategoryCodeLength);
        builder.Property(e => e.DisplayName)
            .IsRequired()
            .HasMaxLength(MLogCategoryConfig.MaxDisplayNameLength);
        builder.Property(e => e.IsEnabled).IsRequired();
        builder.Property(e => e.MinSeverity)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);
        builder.Property(e => e.UpdatedByUserId);

        builder.HasIndex(e => e.CategoryCode)
            .IsUnique()
            .HasDatabaseName("UX_MLogCategoryConfigs_CategoryCode");

        builder.HasIndex(e => new { e.IsEnabled, e.IsActive })
            .HasDatabaseName("IX_MLogCategoryConfigs_IsEnabled_IsActive");
    }
}
