using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2501 / TOR PIR 024 — maps <see cref="BusinessHoursPolicy"/> to
/// <c>cnas.BusinessHoursPolicies</c>. Enforces unique <c>Code</c> and an
/// index on <c>IsActive</c>.
/// </summary>
public sealed class BusinessHoursPolicyConfiguration : AuditableEntityConfiguration<BusinessHoursPolicy>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<BusinessHoursPolicy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BusinessHoursPolicies");

        builder.Property(e => e.Code).IsRequired().HasMaxLength(64);
        builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasMaxLength(1000);
        builder.Property(e => e.OpenTimeLocal).IsRequired();
        builder.Property(e => e.CloseTimeLocal).IsRequired();
        builder.Property(e => e.BusinessDaysMask).IsRequired().HasDefaultValue(31);
        builder.Property(e => e.TimezoneId).IsRequired().HasMaxLength(64);
        builder.Property(e => e.HolidayDatesJson).HasMaxLength(8000);
        builder.Property(e => e.RegisteredByUserId).IsRequired();

        builder.HasIndex(e => e.Code)
            .IsUnique()
            .HasDatabaseName("UX_BusinessHoursPolicies_Code");
    }
}
