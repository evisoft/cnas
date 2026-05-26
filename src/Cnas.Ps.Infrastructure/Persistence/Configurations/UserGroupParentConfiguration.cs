using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2270 / TOR SEC 023-024 — maps <see cref="UserGroupParent"/> to
/// <c>cnas.UserGroupParents</c>. Composite primary key
/// <c>(ParentGroupId, ChildGroupId)</c> mirrors the natural-key uniqueness;
/// a DB CHECK constraint forbids the degenerate self-loop where the two
/// columns point at the same row.
/// </summary>
/// <remarks>
/// <para>
/// Self-loop prevention is enforced at two layers:
/// <list type="bullet">
///   <item>
///   <b>Service layer</b> — <c>IUserGroupService.AddChildAsync</c> returns a
///   conflict before the row reaches the persistence pipeline.
///   </item>
///   <item>
///   <b>Database</b> — the CHECK constraint declared below acts as a
///   defence-in-depth safety net (CLAUDE.md cross-cutting "trust but verify").
///   </item>
/// </list>
/// Cycle prevention beyond the self-loop case lives entirely in the service
/// layer because PostgreSQL does not express transitive-cycle constraints
/// natively without a trigger.
/// </para>
/// </remarks>
public sealed class UserGroupParentConfiguration : IEntityTypeConfiguration<UserGroupParent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserGroupParent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserGroupParents", t =>
            t.HasCheckConstraint(
                "CK_UserGroupParents_NoSelfLoop",
                "\"ParentGroupId\" <> \"ChildGroupId\""));

        // Composite primary key.
        builder.HasKey(p => new { p.ParentGroupId, p.ChildGroupId });

        // Surrogate Id from AuditableEntity is not the primary key on this
        // join table — drop it from the model so EF does not require a
        // value-generated identity column.
        builder.Ignore(p => p.Id);

        builder.Property(p => p.ParentGroupId).IsRequired();
        builder.Property(p => p.ChildGroupId).IsRequired();
        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.CreatedBy).HasMaxLength(64);
        builder.Property(p => p.UpdatedBy).HasMaxLength(64);
        builder.Property(p => p.IsActive).IsRequired().HasDefaultValue(true);

        builder.HasOne(p => p.ParentGroup)
            .WithMany(g => g.Children)
            .HasForeignKey(p => p.ParentGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.ChildGroup)
            .WithMany(g => g.Parents)
            .HasForeignKey(p => p.ChildGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        // Reverse-lookup index — "who are this child's parents?".
        builder.HasIndex(p => p.ChildGroupId)
            .HasDatabaseName("IX_UserGroupParents_ChildGroupId");
    }
}
