using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2273 / TOR SEC 027 — EF Core configuration for
/// <see cref="SensitiveAdminAction"/>. Maps the entity to
/// <c>cnas.SensitiveAdminActions</c> with the indexes required by the operator
/// dashboards and the expiry sweeper.
/// </summary>
/// <remarks>
/// <para>
/// Indexes contributed by this configuration (in addition to the soft-delete + audit
/// timestamps from <see cref="AuditableEntityConfiguration{TEntity}"/>):
/// <list type="bullet">
///   <item><description><c>(Status, ExpiresAt)</c> — fast scan for the expiry sweeper's <c>WHERE Status='PendingApproval' AND ExpiresAt &lt; now</c>.</description></item>
///   <item><description><c>(ActionCode, Status)</c> — supports the operator dashboards' "open requests for action X" filter.</description></item>
///   <item><description><c>(RequestedByUserId, RequestedAt DESC)</c> — supports "my submitted requests" lookups.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>JSON payload columns.</b> <see cref="SensitiveAdminAction.RequestPayloadJson"/> /
/// <see cref="SensitiveAdminAction.ExecutionResultJson"/> are stored as PostgreSQL
/// <c>text</c> — they are round-tripped verbatim and never queried by JSON-path at this
/// layer. Same trade-off as <c>PendingAdminActionConfiguration</c> /
/// <c>WorkflowDefinitionConfiguration</c>.
/// </para>
/// </remarks>
public sealed class SensitiveAdminActionConfiguration : AuditableEntityConfiguration<SensitiveAdminAction>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SensitiveAdminAction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SensitiveAdminActions");

        builder.Property(p => p.ActionCode).IsRequired().HasMaxLength(64);

        // Enum stored as stable name string so a re-ordering of the enum values would not
        // silently change DB-stored statuses.
        builder.Property(p => p.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(p => p.RequestedByUserId).IsRequired();
        builder.Property(p => p.RequestedAt).IsRequired();
        builder.Property(p => p.RequestReason).IsRequired().HasMaxLength(1000);
        builder.Property(p => p.RequestPayloadJson)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(p => p.ApprovedByUserId);
        builder.Property(p => p.ApprovedAt);
        builder.Property(p => p.ApprovalNote).HasMaxLength(1000);

        builder.Property(p => p.RejectedByUserId);
        builder.Property(p => p.RejectedAt);
        builder.Property(p => p.RejectionReason).HasMaxLength(1000);

        builder.Property(p => p.CancelledAt);
        builder.Property(p => p.CancelReason).HasMaxLength(1000);

        builder.Property(p => p.ExpiresAt).IsRequired();

        builder.Property(p => p.ExecutedAt);
        builder.Property(p => p.ExecutionResultJson).HasColumnType("text");
        builder.Property(p => p.ExecutionFailureReason).HasMaxLength(1000);

        builder.HasIndex(p => new { p.Status, p.ExpiresAt });
        builder.HasIndex(p => new { p.ActionCode, p.Status });
        builder.HasIndex(p => new { p.RequestedByUserId, p.RequestedAt })
            .IsDescending(false, true);
    }
}
