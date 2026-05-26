using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0362 — maps <see cref="ProfileUpdateRequest"/> to <c>cnas.ProfileUpdateRequests</c>.
/// Enforces one-to-one with <see cref="ServiceApplication"/> via a unique index on
/// <c>ServiceApplicationId</c>; carries a (TargetContributorId, Status) index for
/// the operator query "pending profile changes for this contributor".
/// </summary>
public sealed class ProfileUpdateRequestConfiguration : AuditableEntityConfiguration<ProfileUpdateRequest>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ProfileUpdateRequest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProfileUpdateRequests");

        builder.Property(e => e.ServiceApplicationId).IsRequired();
        builder.Property(e => e.TargetContributorId).IsRequired();
        builder.Property(e => e.Type).IsRequired();
        builder.Property(e => e.RequestedChangesJson).IsRequired();
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.RejectionReason).HasMaxLength(1024);
        // ApplicationErrorJson is unbounded JSON-shape text — keep the column open.

        builder.HasIndex(e => e.ServiceApplicationId)
            .IsUnique()
            .HasDatabaseName("UX_ProfileUpdateRequests_ServiceApplicationId");
        builder.HasIndex(e => new { e.TargetContributorId, e.Status })
            .HasDatabaseName("IX_ProfileUpdateRequests_Contributor_Status");
    }
}
