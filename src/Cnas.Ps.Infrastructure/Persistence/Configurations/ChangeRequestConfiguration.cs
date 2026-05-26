using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2505 / TOR PIR 030-033 — maps <see cref="ChangeRequest"/> to
/// <c>cnas.ChangeRequests</c>. Enforces unique <c>ChangeNumber</c> and a
/// composite index on <c>(Status, Kind)</c>.
/// </summary>
public sealed class ChangeRequestConfiguration : AuditableEntityConfiguration<ChangeRequest>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ChangeRequest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ChangeRequests");

        builder.Property(e => e.ChangeNumber).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).IsRequired().HasMaxLength(8000);
        builder.Property(e => e.Kind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Risk)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.ImpactedSystems).IsRequired().HasMaxLength(1000);
        builder.Property(e => e.RollbackPlan).IsRequired().HasMaxLength(4000);
        builder.Property(e => e.TestEnvironmentValidationNote).HasMaxLength(2000);
        builder.Property(e => e.TestValidatedByUserId);
        builder.Property(e => e.TestValidatedAt);
        builder.Property(e => e.CodeSignatureReference).HasMaxLength(128);
        builder.Property(e => e.CodeSignedByUserId);
        builder.Property(e => e.CodeSignedAt);
        builder.Property(e => e.RequestedByUserId).IsRequired();
        builder.Property(e => e.ApprovedByUserId);
        builder.Property(e => e.ApprovedAt);
        builder.Property(e => e.DeployedAt);
        builder.Property(e => e.RolledBackAt);
        builder.Property(e => e.RollbackReason).HasMaxLength(2000);
        builder.Property(e => e.CancelReason).HasMaxLength(500);
        builder.Property(e => e.RelatedMaintenanceWindowId);

        builder.HasIndex(e => e.ChangeNumber)
            .IsUnique()
            .HasDatabaseName("UX_ChangeRequests_ChangeNumber");

        builder.HasIndex(e => new { e.Status, e.Kind })
            .HasDatabaseName("IX_ChangeRequests_Status_Kind");
    }
}
