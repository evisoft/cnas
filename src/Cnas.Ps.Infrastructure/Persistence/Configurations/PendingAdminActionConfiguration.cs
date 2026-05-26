using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="PendingAdminAction"/> to <c>cnas.PendingAdminActions</c> — the
/// authoritative store for sensitive admin actions awaiting a second-administrator
/// approval (R0058 / SEC 027). Domain-specific indexes (in addition to the soft-delete +
/// audit-timestamp indexes contributed by
/// <see cref="AuditableEntityConfiguration{TEntity}"/>):
/// <list type="bullet">
///   <item><description><c>(Status)</c> — supports the "list pending" query and the expiry sweeper.</description></item>
///   <item><description><c>(ExpiresAtUtc)</c> — supports the expiry sweeper's <c>WHERE ExpiresAtUtc &lt; now</c>.</description></item>
///   <item><description><c>(MakerUserId)</c> — supports "actions I submitted" lookups.</description></item>
///   <item><description><c>(CheckerUserId)</c> — supports "actions I approved" lookups.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>PayloadJson column.</b> Mapped as PostgreSQL <c>text</c> (not <c>jsonb</c>)
/// because the payload is round-tripped verbatim and never queried by JSON-path
/// expressions at this layer — same trade-off as
/// <see cref="WorkflowDefinitionConfiguration"/>'s <c>DefinitionJson</c>.
/// </para>
/// <para>
/// <b>Operation length cap.</b> 64 characters mirrors the cap used on
/// <c>WorkflowDefinition.Code</c>; operation codes are SCREAMING_SNAKE_CASE and the
/// longest plausible entry today (<c>USER.SUSPEND_TEMPORARILY_PENDING_REVIEW</c>) sits
/// well below the limit.
/// </para>
/// </remarks>
public sealed class PendingAdminActionConfiguration : AuditableEntityConfiguration<PendingAdminAction>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PendingAdminAction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PendingAdminActions");

        builder.Property(p => p.Operation).IsRequired().HasMaxLength(64);

        // text rather than jsonb — see class-level remarks for the rationale.
        builder.Property(p => p.PayloadJson)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(p => p.MakerUserId).IsRequired();
        builder.Property(p => p.MakerRequestedAtUtc).IsRequired();
        builder.Property(p => p.CheckerUserId);
        builder.Property(p => p.CheckerDecidedAtUtc);
        builder.Property(p => p.Status).IsRequired();
        builder.Property(p => p.RejectionReason).HasMaxLength(512);
        builder.Property(p => p.ExpiresAtUtc).IsRequired();

        // List-pending + sweeper hot paths.
        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.ExpiresAtUtc);

        // "Who-did-what" lookups for the operations audit dashboard.
        builder.HasIndex(p => p.MakerUserId);
        builder.HasIndex(p => p.CheckerUserId);
    }
}
