using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2502 / TOR PIR 025 — maps <see cref="MaintenanceWindow"/> to
/// <c>cnas.MaintenanceWindows</c>. Enforces unique <c>WindowNumber</c>,
/// indexes on <c>(Status, ScheduledStartUtc)</c> and
/// <c>(WindowKind, ScheduledStartUtc)</c>.
/// </summary>
public sealed class MaintenanceWindowConfiguration : AuditableEntityConfiguration<MaintenanceWindow>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MaintenanceWindow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MaintenanceWindows");

        builder.Property(e => e.WindowNumber).IsRequired().HasMaxLength(32);
        builder.Property(e => e.BusinessHoursPolicyId).IsRequired();
        builder.Property(e => e.WindowKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).IsRequired().HasMaxLength(2000);
        builder.Property(e => e.ScheduledStartUtc).IsRequired();
        builder.Property(e => e.ScheduledEndUtc).IsRequired();
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.RequestedByUserId).IsRequired();
        builder.Property(e => e.ApprovedByUserId);
        builder.Property(e => e.NoticePostedAt);
        builder.Property(e => e.ApprovedAt);
        builder.Property(e => e.StartedAt);
        builder.Property(e => e.CompletedAt);
        builder.Property(e => e.CancelledAt);
        builder.Property(e => e.CancelReason).HasMaxLength(500);

        builder.HasIndex(e => e.WindowNumber)
            .IsUnique()
            .HasDatabaseName("UX_MaintenanceWindows_WindowNumber");

        builder.HasIndex(e => new { e.Status, e.ScheduledStartUtc })
            .HasDatabaseName("IX_MaintenanceWindows_Status_ScheduledStartUtc");

        builder.HasIndex(e => new { e.WindowKind, e.ScheduledStartUtc })
            .HasDatabaseName("IX_MaintenanceWindows_WindowKind_ScheduledStartUtc");
    }
}
