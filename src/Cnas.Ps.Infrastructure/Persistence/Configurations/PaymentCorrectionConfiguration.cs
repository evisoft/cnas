using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0815 / TOR BP 1.2-F — maps <see cref="PaymentCorrection"/> to
/// <c>cnas.PaymentCorrections</c>. A non-unique index on
/// <see cref="PaymentCorrection.OriginalTreasuryPaymentReceiptId"/> backs the
/// per-receipt history view; corrections are append-only, so multiple
/// (even cancelled) rows can reference the same receipt over time.
/// </summary>
public sealed class PaymentCorrectionConfiguration : AuditableEntityConfiguration<PaymentCorrection>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PaymentCorrection> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PaymentCorrections");

        builder.Property(e => e.OriginalTreasuryPaymentReceiptId).IsRequired();
        builder.Property(e => e.RedirectedToContributorId);
        builder.Property(e => e.RedirectedToMonth);
        builder.Property(e => e.Kind).IsRequired().HasConversion<int>();
        builder.Property(e => e.AdjustedAmount).HasPrecision(18, 2);
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.RequestedByUserId).IsRequired();
        builder.Property(e => e.ApprovedByUserId);
        builder.Property(e => e.Reason).IsRequired().HasMaxLength(500);
        builder.Property(e => e.CreatedUtc).IsRequired();
        builder.Property(e => e.AppliedUtc);
        builder.Property(e => e.CancelReason).HasMaxLength(500);

        // Per-receipt lookup index — auditors verifying the correction
        // history of a specific receipt scan this index.
        builder.HasIndex(e => e.OriginalTreasuryPaymentReceiptId)
            .HasDatabaseName("IX_PaymentCorrections_OriginalReceipt");

        // Status drain index — operator dashboards filter by Status.
        builder.HasIndex(e => e.Status)
            .HasDatabaseName("IX_PaymentCorrections_Status");
    }
}
