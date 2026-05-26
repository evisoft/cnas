using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0813 — maps <see cref="MonthlyContributionCalculation"/> to
/// <c>cnas.MonthlyContributionCalculations</c>. A composite unique index on
/// <c>(ContributorId, Month)</c> enforces the natural-key rule documented on the
/// entity ("one calculation per payer per month") and doubles as the idempotency
/// key for re-runs. A secondary index on <c>(Month, ContributorId)</c> backs
/// reporting queries that filter by month first ("all calculations for May 2026").
/// </summary>
public sealed class MonthlyContributionCalculationConfiguration
    : AuditableEntityConfiguration<MonthlyContributionCalculation>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MonthlyContributionCalculation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MonthlyContributionCalculations");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.Month).IsRequired();
        builder.Property(e => e.TotalDeclared).HasPrecision(18, 2);
        builder.Property(e => e.TotalAdjusted).HasPrecision(18, 2);
        builder.Property(e => e.OverpaymentAmount).HasPrecision(18, 2);
        builder.Property(e => e.UnderpaymentAmount).HasPrecision(18, 2);
        builder.Property(e => e.DeclarationCount).IsRequired();
        builder.Property(e => e.CalculatedAtUtc).IsRequired();

        // Natural-key uniqueness — one calculation per (payer, month). Idempotent
        // re-runs upsert in place; soft-deleted rows still hold their slot, and the
        // service layer reactivates rather than re-inserts.
        builder.HasIndex(e => new { e.ContributorId, e.Month })
            .IsUnique()
            .HasDatabaseName("UX_MonthlyContributionCalculations_NaturalKey");

        // Reporting index — operator queries that filter by month first.
        builder.HasIndex(e => new { e.Month, e.ContributorId })
            .HasDatabaseName("IX_MonthlyContributionCalculations_Month_Contributor");
    }
}
