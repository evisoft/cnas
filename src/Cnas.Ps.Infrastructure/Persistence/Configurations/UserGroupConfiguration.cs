using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2270 / TOR SEC 023-024 — maps <see cref="UserGroup"/> to
/// <c>cnas.UserGroups</c>. A unique index on
/// <see cref="UserGroup.Code"/> enforces system-wide code uniqueness.
/// </summary>
/// <remarks>
/// <para>
/// Enum columns persist as stable enum-name strings (mirrors the
/// <see cref="ExecutoryDocumentConfiguration"/> pattern) so humans can read
/// the rows directly and the persistence contract is decoupled from the
/// underlying integer values.
/// </para>
/// <para>
/// The <see cref="UserGroup.Roles"/> list is stored as a
/// <c>text[]</c> column on PostgreSQL — mirroring
/// <see cref="UserProfileConfiguration"/>'s treatment of
/// <see cref="UserProfile.Roles"/>.
/// </para>
/// </remarks>
public sealed class UserGroupConfiguration : AuditableEntityConfiguration<UserGroup>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<UserGroup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserGroups");

        builder.Property(g => g.Code).IsRequired().HasMaxLength(64);
        builder.Property(g => g.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(g => g.Description).HasMaxLength(1000);

        builder.Property(g => g.Kind)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        builder.Property(g => g.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        // Roles round-trip through PostgreSQL's text[] (mirrors UserProfile.Roles).
        builder.Property(g => g.Roles)
            .HasColumnType("text[]")
            .IsRequired();

        // Unique code per system.
        builder.HasIndex(g => g.Code)
            .IsUnique()
            .HasDatabaseName("UX_UserGroups_Code");

        // Operator dashboard index — "all Active groups of kind X".
        builder.HasIndex(g => new { g.Status, g.Kind })
            .HasDatabaseName("IX_UserGroups_Status_Kind");
    }
}
