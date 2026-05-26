using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1202 / TOR §3.4-C — maps <see cref="CapitalisedPaymentDecision"/> to
/// <c>cnas.CapitalisedPaymentDecisions</c>. Indexed by
/// <see cref="CapitalisedPaymentDecision.RequestId"/> for per-request lookup
/// and by <c>ComputedAtUtc DESC</c> to support the "latest decision" query
/// path.
/// </summary>
public sealed class CapitalisedPaymentDecisionConfiguration
    : AuditableEntityConfiguration<CapitalisedPaymentDecision>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<CapitalisedPaymentDecision> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CapitalisedPaymentDecisions");

        builder.Property(e => e.RequestId).IsRequired();
        builder.Property(e => e.DecisionStatus)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        builder.Property(e => e.ComputedAtUtc).IsRequired();
        builder.Property(e => e.EffectiveAgeYears).HasPrecision(6, 2);
        builder.Property(e => e.LifeExpectancyMonths).IsRequired();
        builder.Property(e => e.EffectiveDiscountMonthly).HasPrecision(12, 8);
        builder.Property(e => e.CapitalisedAmountMdl).HasPrecision(18, 2);
        builder.Property(e => e.ComputationBreakdownJson).IsRequired().HasMaxLength(32768);
        builder.Property(e => e.ApprovedByUserId);
        builder.Property(e => e.RejectionReason).HasMaxLength(1000);

        builder.HasIndex(e => e.RequestId)
            .HasDatabaseName("IX_CapitalisedPaymentDecisions_RequestId");

        builder.HasIndex(e => e.ComputedAtUtc)
            .IsDescending(true)
            .HasDatabaseName("IX_CapitalisedPaymentDecisions_ComputedAtUtc");
    }
}
