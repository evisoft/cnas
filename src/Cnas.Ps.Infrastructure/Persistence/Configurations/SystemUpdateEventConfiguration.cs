using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2504 / TOR PIR 024 — maps <see cref="SystemUpdateEvent"/> to
/// <c>cnas.SystemUpdateEvents</c>. Enforces unique <c>EventNumber</c> and
/// composite indexes on <c>(Status, PlannedDeploymentUtc)</c> and
/// <c>(ScheduleId, PlannedDeploymentUtc)</c>.
/// </summary>
public sealed class SystemUpdateEventConfiguration : AuditableEntityConfiguration<SystemUpdateEvent>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SystemUpdateEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SystemUpdateEvents");

        builder.Property(e => e.ScheduleId).IsRequired();
        builder.Property(e => e.EventNumber).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasMaxLength(2000);
        builder.Property(e => e.PlannedDeploymentUtc).IsRequired();
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.NotifiedAt);
        builder.Property(e => e.DeploymentStartedAt);
        builder.Property(e => e.DeploymentCompletedAt);
        builder.Property(e => e.CancelledAt);
        builder.Property(e => e.CancelReason).HasMaxLength(500);
        builder.Property(e => e.MaintenanceWindowId);

        builder.HasIndex(e => e.EventNumber)
            .IsUnique()
            .HasDatabaseName("UX_SystemUpdateEvents_EventNumber");

        builder.HasIndex(e => new { e.Status, e.PlannedDeploymentUtc })
            .HasDatabaseName("IX_SystemUpdateEvents_Status_PlannedDeploymentUtc");

        builder.HasIndex(e => new { e.ScheduleId, e.PlannedDeploymentUtc })
            .HasDatabaseName("IX_SystemUpdateEvents_ScheduleId_PlannedDeploymentUtc");
    }
}
