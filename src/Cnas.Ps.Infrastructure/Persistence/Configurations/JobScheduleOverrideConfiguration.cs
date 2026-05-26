using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0200 / TOR CF 20.01-03, MR 012 — maps <see cref="JobScheduleOverride"/> to
/// <c>cnas.JobScheduleOverrides</c>. Enforces a unique <c>JobCode</c> (one override per
/// Quartz job) plus an index on <c>IsPaused</c> for the applicator's "find all
/// currently-paused jobs" scan.
/// </summary>
public sealed class JobScheduleOverrideConfiguration
    : AuditableEntityConfiguration<JobScheduleOverride>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<JobScheduleOverride> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("JobScheduleOverrides");

        builder.Property(e => e.JobCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.CronExpression).IsRequired().HasMaxLength(200);
        builder.Property(e => e.IsPaused).IsRequired();
        builder.Property(e => e.UpdatedByUserId);

        builder.HasIndex(e => e.JobCode)
            .IsUnique()
            .HasDatabaseName("UX_JobScheduleOverrides_JobCode");

        builder.HasIndex(e => e.IsPaused)
            .HasDatabaseName("IX_JobScheduleOverrides_IsPaused");
    }
}
