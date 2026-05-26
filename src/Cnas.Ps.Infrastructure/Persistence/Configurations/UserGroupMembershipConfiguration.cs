using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2270 / TOR SEC 023-024 — maps <see cref="UserGroupMembership"/> to
/// <c>cnas.UserGroupMemberships</c>. Composite primary key
/// <c>(UserGroupId, UserProfileId)</c>. The reverse-lookup index on
/// <c>UserProfileId</c> backs the per-user effective-role resolver path
/// that asks "which groups is this user a member of?".
/// </summary>
public sealed class UserGroupMembershipConfiguration : IEntityTypeConfiguration<UserGroupMembership>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserGroupMembership> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserGroupMemberships");

        builder.HasKey(m => new { m.UserGroupId, m.UserProfileId });

        // The surrogate Id from AuditableEntity is not the primary key on
        // this join table — drop it so EF does not require an identity column.
        builder.Ignore(m => m.Id);

        builder.Property(m => m.UserGroupId).IsRequired();
        builder.Property(m => m.UserProfileId).IsRequired();
        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.CreatedBy).HasMaxLength(64);
        builder.Property(m => m.UpdatedBy).HasMaxLength(64);
        builder.Property(m => m.IsActive).IsRequired().HasDefaultValue(true);

        builder.HasOne(m => m.UserGroup)
            .WithMany(g => g.Memberships)
            .HasForeignKey(m => m.UserGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.UserProfile)
            .WithMany()
            .HasForeignKey(m => m.UserProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        // Reverse-lookup index — "which groups is this user a member of?".
        builder.HasIndex(m => m.UserProfileId)
            .HasDatabaseName("IX_UserGroupMemberships_UserProfileId");
    }
}
