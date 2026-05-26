using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2503 / TOR PIR 022-023 — maps <see cref="SystemUpdateSchedule"/> to
/// <c>cnas.SystemUpdateSchedules</c>. Enforces unique <c>ScheduleCode</c> and
/// a composite index on <c>(IsActive, Cadence)</c>.
/// </summary>
public sealed class SystemUpdateScheduleConfiguration : AuditableEntityConfiguration<SystemUpdateSchedule>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SystemUpdateSchedule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SystemUpdateSchedules");

        builder.Property(e => e.ScheduleCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Cadence)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.NoticeLeadTimeDays).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(2000);
        builder.Property(e => e.RegisteredByUserId).IsRequired();

        builder.HasIndex(e => e.ScheduleCode)
            .IsUnique()
            .HasDatabaseName("UX_SystemUpdateSchedules_ScheduleCode");

        builder.HasIndex(e => new { e.IsActive, e.Cadence })
            .HasDatabaseName("IX_SystemUpdateSchedules_IsActive_Cadence");
    }
}
