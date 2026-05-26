using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0817 / TOR BP 1.2-H — maps <see cref="PenaltyRepaymentPlan"/> to
/// <c>cnas.PenaltyRepaymentPlans</c>. A filtered unique index on
/// <see cref="PenaltyRepaymentPlan.LatePaymentPenaltyId"/> WHERE
/// <c>Status = 0</c> (Active) enforces the "at most one Active plan per
/// penalty" invariant documented on the entity. A secondary index on
/// (<c>Status</c>) backs the background-detection job's drain query.
/// </summary>
public sealed class PenaltyRepaymentPlanConfiguration
    : AuditableEntityConfiguration<PenaltyRepaymentPlan>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PenaltyRepaymentPlan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PenaltyRepaymentPlans");

        builder.Property(e => e.LatePaymentPenaltyId).IsRequired();
        builder.Property(e => e.InstallmentCount).IsRequired();
        builder.Property(e => e.InstallmentAmount).HasPrecision(18, 2);
        builder.Property(e => e.FirstInstallmentDueDate).IsRequired();
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.PaidInstallmentCount).IsRequired();
        builder.Property(e => e.RemainingAmount).HasPrecision(18, 2);
        builder.Property(e => e.CreatedUtc).IsRequired();
        builder.Property(e => e.CompletedUtc);
        builder.Property(e => e.CancelledUtc);
        builder.Property(e => e.CancelReason).HasMaxLength(500);

        // Active-uniqueness — at most one Active plan per penalty. The filter
        // uses the persisted enum int value (0 = Active) so Postgres can prune
        // the index rows efficiently.
        builder.HasIndex(e => e.LatePaymentPenaltyId)
            .IsUnique()
            .HasFilter("\"Status\" = 0")
            .HasDatabaseName("UX_PenaltyRepaymentPlans_Penalty_Active");

        // Status drain index — the background default-detector filters by Status.
        builder.HasIndex(e => e.Status)
            .HasDatabaseName("IX_PenaltyRepaymentPlans_Status");
    }
}

/// <summary>
/// R0817 / TOR BP 1.2-H — maps <see cref="PenaltyRepaymentInstallment"/> to
/// <c>cnas.PenaltyRepaymentInstallments</c>. A composite unique index on
/// (<see cref="PenaltyRepaymentInstallment.PenaltyRepaymentPlanId"/>,
/// <see cref="PenaltyRepaymentInstallment.InstallmentNumber"/>) enforces the
/// natural-key rule documented on the entity. A secondary index on
/// (<c>PenaltyRepaymentPlanId</c>, <c>DueDate</c>) backs the per-plan
/// schedule-view ordered by due-date ascending.
/// </summary>
public sealed class PenaltyRepaymentInstallmentConfiguration
    : AuditableEntityConfiguration<PenaltyRepaymentInstallment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PenaltyRepaymentInstallment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PenaltyRepaymentInstallments");

        builder.Property(e => e.PenaltyRepaymentPlanId).IsRequired();
        builder.Property(e => e.InstallmentNumber).IsRequired();
        builder.Property(e => e.DueDate).IsRequired();
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.PaidDate);
        builder.Property(e => e.PaidAmount).HasPrecision(18, 2);
        builder.Property(e => e.IsPaid).IsRequired().HasDefaultValue(false);

        // Natural-key uniqueness — one installment per (plan, position).
        builder.HasIndex(e => new { e.PenaltyRepaymentPlanId, e.InstallmentNumber })
            .IsUnique()
            .HasDatabaseName("UX_PenaltyRepaymentInstallments_Plan_Number");

        // Per-plan schedule index — admin UI orders by DueDate ascending.
        builder.HasIndex(e => new { e.PenaltyRepaymentPlanId, e.DueDate })
            .HasDatabaseName("IX_PenaltyRepaymentInstallments_Plan_DueDate");
    }
}
