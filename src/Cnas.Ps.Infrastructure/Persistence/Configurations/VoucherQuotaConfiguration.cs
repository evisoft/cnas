using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1000..R1034 / TOR §3.2-AB..AD — EF Core configuration for
/// <see cref="VoucherQuota"/>. Maps the entity to <c>cnas.VoucherQuotas</c>
/// and enforces the natural-key (PassportCode, Year) uniqueness via a
/// filtered partial index scoped to <see cref="AuditableEntity.IsActive"/>
/// = <c>true</c> so deactivated rows do not collide with new seedings.
/// </summary>
public sealed class VoucherQuotaConfiguration : AuditableEntityConfiguration<VoucherQuota>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<VoucherQuota> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("VoucherQuotas");

        builder.Property(e => e.PassportCode).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Year).IsRequired();
        builder.Property(e => e.MonthlyQuota).IsRequired().HasDefaultValue(0);
        builder.Property(e => e.AnnualQuota).IsRequired().HasDefaultValue(0);
        builder.Property(e => e.UsedThisMonth).IsRequired().HasDefaultValue(0);
        builder.Property(e => e.UsedThisYear).IsRequired().HasDefaultValue(0);
        builder.Property(e => e.UsedMonth).IsRequired().HasDefaultValue(0);

        builder.HasIndex(e => new { e.PassportCode, e.Year })
            .IsUnique()
            .HasFilter("\"IsActive\" = true")
            .HasDatabaseName("UX_VoucherQuotas_PassportCode_Year_Active");
    }
}
