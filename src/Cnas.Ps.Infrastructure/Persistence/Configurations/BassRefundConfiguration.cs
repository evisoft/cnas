using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0814 / TOR BP 1.2-E — maps <see cref="BassRefund"/> to
/// <c>cnas.BassRefunds</c>. A filtered unique index on
/// <c>(ContributorId, RelatedMonth) WHERE Status &lt;&gt; Cancelled</c>
/// enforces the "at most one active refund per (payer, month)" invariant
/// documented on the entity. A secondary index on <c>(Status)</c> backs the
/// operator-dashboard "pending refunds" view.
/// </summary>
public sealed class BassRefundConfiguration : AuditableEntityConfiguration<BassRefund>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<BassRefund> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BassRefunds");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.RelatedMonth).IsRequired();
        builder.Property(e => e.RefundAmount).HasPrecision(18, 2);
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.AuthorisationDocumentReference).HasMaxLength(256);
        builder.Property(e => e.RequestedByUserId).IsRequired();
        builder.Property(e => e.ApprovedByUserId);
        builder.Property(e => e.ApprovedDate);
        builder.Property(e => e.TreasuryDispatchReference).HasMaxLength(64);
        builder.Property(e => e.IssuedDate);
        builder.Property(e => e.ConfirmedDate);
        builder.Property(e => e.CancelReason).HasMaxLength(500);
        builder.Property(e => e.CancelledDate);

        // Filtered uniqueness — at most one non-Cancelled refund per
        // (payer, month). The filter uses the persisted enum int value (4
        // = Cancelled) so Postgres can prune the index rows efficiently.
        builder.HasIndex(e => new { e.ContributorId, e.RelatedMonth })
            .IsUnique()
            .HasFilter("\"Status\" <> 4")
            .HasDatabaseName("UX_BassRefunds_Contributor_Month_Active");

        // Status drain index — operator dashboards filter by Status.
        builder.HasIndex(e => e.Status)
            .HasDatabaseName("IX_BassRefunds_Status");
    }
}
