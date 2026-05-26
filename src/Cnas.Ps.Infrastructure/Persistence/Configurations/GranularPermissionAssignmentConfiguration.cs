using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0673 / TOR CF 18.12 — maps <see cref="GranularPermissionAssignment"/> to
/// <c>cnas.GranularPermissionAssignments</c>. Enforces a unique
/// <c>(RoleCode, ResourceType, PermissionVerb)</c> tuple (one grant per triple)
/// plus a covering index on <c>(RoleCode, ResourceType)</c> for the per-call
/// "does this role have any verb on this resource" lookup.
/// </summary>
public sealed class GranularPermissionAssignmentConfiguration
    : AuditableEntityConfiguration<GranularPermissionAssignment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<GranularPermissionAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("GranularPermissionAssignments");

        builder.Property(e => e.RoleCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ResourceType).IsRequired().HasMaxLength(64);
        builder.Property(e => e.PermissionVerb).IsRequired().HasMaxLength(32);
        builder.Property(e => e.GrantedAtUtc).IsRequired();
        builder.Property(e => e.GrantedByUserId);

        builder.HasIndex(e => new { e.RoleCode, e.ResourceType, e.PermissionVerb })
            .IsUnique()
            .HasDatabaseName("UX_GranularPermissionAssignments_Triple");

        builder.HasIndex(e => new { e.RoleCode, e.ResourceType })
            .HasDatabaseName("IX_GranularPermissionAssignments_RoleResource");
    }
}
