using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0831 / BP 1.3-B — maps <see cref="Claim"/> to <c>cnas.Claims</c>. A unique
/// index on <see cref="Claim.ClaimNumber"/> enforces the natural-key rule
/// (every claim carries a stable external identifier). A secondary index on
/// <c>(ContributorId, Status)</c> backs the per-payer dashboard and the
/// outstanding-claims report.
/// </summary>
public sealed class ClaimConfiguration : AuditableEntityConfiguration<Claim>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Claim> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Claims");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.ClaimNumber).IsRequired().HasMaxLength(32);
        builder.Property(e => e.RelatedMonth).IsRequired();
        builder.Property(e => e.Kind).IsRequired().HasConversion<int>();
        builder.Property(e => e.PrincipalAmount).HasPrecision(18, 2);
        builder.Property(e => e.PaidAmount).HasPrecision(18, 2);
        builder.Property(e => e.RemainingAmount).HasPrecision(18, 2);
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.OpenedDate).IsRequired();
        builder.Property(e => e.DueDate);
        builder.Property(e => e.SettledDate);
        builder.Property(e => e.CancelledDate);
        builder.Property(e => e.CancelReason).HasMaxLength(500);
        builder.Property(e => e.RelatedDocumentReference).HasMaxLength(256);

        // Natural-key uniqueness — one claim per external identifier.
        builder.HasIndex(e => e.ClaimNumber)
            .IsUnique()
            .HasDatabaseName("UX_Claims_ClaimNumber");

        // Reporting index — operator dashboards filter by (payer, status).
        builder.HasIndex(e => new { e.ContributorId, e.Status })
            .HasDatabaseName("IX_Claims_Contributor_Status");
    }
}

/// <summary>
/// R0832 / BP 1.3-C — maps <see cref="ClaimPayment"/> to
/// <c>cnas.ClaimPayments</c>. A composite unique index on
/// <c>(ClaimId, PaymentReference)</c> prevents the same external reference
/// from being registered twice against the same claim (the index uses a
/// filter so null references are excluded). A secondary index on
/// <c>(ClaimId, PaidDate DESC)</c> backs the per-claim payments-history view.
/// </summary>
public sealed class ClaimPaymentConfiguration : AuditableEntityConfiguration<ClaimPayment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ClaimPayment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ClaimPayments");

        builder.Property(e => e.ClaimId).IsRequired();
        builder.Property(e => e.PaidDate).IsRequired();
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.PaymentReference).HasMaxLength(64);
        builder.Property(e => e.TreasuryPaymentReceiptId);
        builder.Property(e => e.Notes).HasMaxLength(1000);

        // Per-claim payment lookup index — the per-claim history view orders
        // by PaidDate DESC.
        builder.HasIndex(e => new { e.ClaimId, e.PaidDate })
            .HasDatabaseName("IX_ClaimPayments_Claim_PaidDate");

        // Uniqueness across (ClaimId, PaymentReference) when reference is set.
        // The filter expression keeps the index narrow on Postgres and avoids
        // collisions when the reference is null.
        builder.HasIndex(e => new { e.ClaimId, e.PaymentReference })
            .IsUnique()
            .HasFilter("\"PaymentReference\" IS NOT NULL")
            .HasDatabaseName("UX_ClaimPayments_Claim_PaymentReference");
    }
}
