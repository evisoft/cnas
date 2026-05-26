using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0820 — maps <see cref="ManagementPeriodClose"/> to
/// <c>cnas.ManagementPeriodCloses</c>. A unique index on <see cref="ManagementPeriodClose.Month"/>
/// enforces the singleton-per-month natural-key rule documented on the entity.
/// </summary>
public sealed class ManagementPeriodCloseConfiguration
    : AuditableEntityConfiguration<ManagementPeriodClose>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ManagementPeriodClose> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ManagementPeriodCloses");

        builder.Property(e => e.Month).IsRequired();
        builder.Property(e => e.ClosedAtUtc).IsRequired();
        builder.Property(e => e.ClosedByUserId).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(1000);
        builder.Property(e => e.TotalDeclaredAcrossPayers).HasPrecision(18, 2);
        builder.Property(e => e.TotalPaidAcrossPayers).HasPrecision(18, 2);
        builder.Property(e => e.PayerCount).IsRequired();
        builder.Property(e => e.DeclarationCount).IsRequired();
        builder.Property(e => e.IsReopened).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.ReopenedAtUtc);
        builder.Property(e => e.ReopenedByUserId);
        builder.Property(e => e.ReopenReason).HasMaxLength(500);

        // Singleton per month — one close row per calendar month.
        builder.HasIndex(e => e.Month)
            .IsUnique()
            .HasDatabaseName("UX_ManagementPeriodCloses_Month");
    }
}
