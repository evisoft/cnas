using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0911 / TOR BP 2.2-B — maps <see cref="TreasuryPaymentReceipt"/> to
/// <c>cnas.TreasuryPaymentReceipts</c>. A unique index on
/// <see cref="TreasuryPaymentReceipt.TreasuryReferenceNumber"/> enforces the
/// natural-key uniqueness rule documented on the entity; the secondary index
/// on <c>(DistributionStatus, ReceiptDate DESC)</c> supports the
/// background-job's drain query path.
/// </summary>
public sealed class TreasuryPaymentReceiptConfiguration
    : AuditableEntityConfiguration<TreasuryPaymentReceipt>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<TreasuryPaymentReceipt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TreasuryPaymentReceipts");

        builder.Property(e => e.TreasuryReferenceNumber).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ReceiptDate).IsRequired();
        builder.Property(e => e.PayerContributorId).IsRequired();
        builder.Property(e => e.ReportingMonth).IsRequired();
        builder.Property(e => e.AmountReceived).HasPrecision(18, 2);
        builder.Property(e => e.DistributionStatus).IsRequired().HasConversion<int>();
        builder.Property(e => e.DistributionFailureReason).HasMaxLength(128);
        builder.Property(e => e.UndistributedRemainderAmount).HasPrecision(18, 2);

        // Natural-key uniqueness: the Treasury reference number is globally
        // unique — re-importing the same reference is a no-op duplicate.
        builder.HasIndex(e => e.TreasuryReferenceNumber)
            .IsUnique()
            .HasDatabaseName("UX_TreasuryPaymentReceipts_Reference");

        // Distribution-job drain index — composite on the status filter +
        // ReceiptDate DESC so the background sweep efficiently picks the
        // oldest Pending receipts first.
        builder.HasIndex(e => new { e.DistributionStatus, e.ReceiptDate })
            .HasDatabaseName("IX_TreasuryPaymentReceipts_Status_Date");

        // Payer / month lookup — auditors verifying contribution distribution
        // for a specific (payer × month) tuple use this path.
        builder.HasIndex(e => new { e.PayerContributorId, e.ReportingMonth })
            .HasDatabaseName("IX_TreasuryPaymentReceipts_Payer_Month");
    }
}
