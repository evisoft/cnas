using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0819 — maps <see cref="LatePaymentPenalty"/> to
/// <c>cnas.LatePaymentPenalties</c>. A composite unique index on
/// <c>(ContributorId, Month, UpToDate)</c> enforces the natural-key rule
/// documented on the entity and doubles as the idempotency key for re-runs.
/// A secondary index on <c>(Month, ContributorId)</c> backs reporting queries
/// that filter by month first ("all penalties for May 2026").
/// </summary>
public sealed class LatePaymentPenaltyConfiguration
    : AuditableEntityConfiguration<LatePaymentPenalty>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<LatePaymentPenalty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LatePaymentPenalties");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.Month).IsRequired();
        builder.Property(e => e.PrincipalAmount).HasPrecision(18, 2);
        builder.Property(e => e.CalculatedAtUtc).IsRequired();
        builder.Property(e => e.DueDate).IsRequired();
        builder.Property(e => e.UpToDate).IsRequired();
        builder.Property(e => e.DaysLate).IsRequired();
        builder.Property(e => e.DailyRatePercent).HasPrecision(9, 6);
        builder.Property(e => e.PenaltyAmount).HasPrecision(18, 2);
        builder.Property(e => e.IsWaived).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.WaiveReason).HasMaxLength(500);

        // Natural-key uniqueness — one penalty per (payer, month, up-to date).
        // Idempotent re-runs upsert in place.
        builder.HasIndex(e => new { e.ContributorId, e.Month, e.UpToDate })
            .IsUnique()
            .HasDatabaseName("UX_LatePaymentPenalties_NaturalKey");

        // Reporting index — operators that filter by month first.
        builder.HasIndex(e => new { e.Month, e.ContributorId })
            .HasDatabaseName("IX_LatePaymentPenalties_Month_Contributor");
    }
}
