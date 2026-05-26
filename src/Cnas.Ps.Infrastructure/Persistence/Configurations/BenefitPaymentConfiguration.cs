using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0517 / TOR CF 02.05 — maps <see cref="BenefitPayment"/> to
/// <c>cnas.BenefitPayments</c>. A composite unique index on
/// <c>(BeneficiarySolicitantId, BenefitType, PaymentMonth)</c> enforces the
/// natural-key rule documented on the entity ("at most one payment per
/// beneficiary per benefit per month"). A secondary index on
/// <c>(BeneficiarySolicitantId, PaymentMonth DESC)</c> backs the
/// authenticated status-lookup query path, which always filters by
/// beneficiary and orders by month descending.
/// </summary>
public sealed class BenefitPaymentConfiguration : AuditableEntityConfiguration<BenefitPayment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<BenefitPayment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BenefitPayments");

        builder.Property(e => e.BeneficiarySolicitantId).IsRequired();
        builder.Property(e => e.BenefitType).IsRequired().HasConversion<int>();
        builder.Property(e => e.PaymentMonth).IsRequired();
        builder.Property(e => e.GrossAmount).HasPrecision(18, 2);
        builder.Property(e => e.NetAmount).HasPrecision(18, 2);
        builder.Property(e => e.TaxWithheld).HasPrecision(18, 2);
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.Method).IsRequired().HasConversion<int>();
        builder.Property(e => e.BankAccountIban).HasMaxLength(34);
        builder.Property(e => e.PostalOrderNumber).HasMaxLength(64);
        builder.Property(e => e.ReturnReason).HasMaxLength(512);

        // Natural-key uniqueness — see the entity remarks for the rationale.
        // Soft-deleted rows still occupy their slot; reissues after a Returned
        // event reuse the same row via a status transition rather than insert.
        builder.HasIndex(e => new { e.BeneficiarySolicitantId, e.BenefitType, e.PaymentMonth })
            .IsUnique()
            .HasDatabaseName("UX_BenefitPayments_NaturalKey");

        // Listing index — the authenticated status-lookup query always filters
        // by the beneficiary and orders by month DESC. Postgres uses the
        // composite index for both the equality + sort phases.
        builder.HasIndex(e => new { e.BeneficiarySolicitantId, e.PaymentMonth })
            .HasDatabaseName("IX_BenefitPayments_Beneficiary_Month");
    }
}
