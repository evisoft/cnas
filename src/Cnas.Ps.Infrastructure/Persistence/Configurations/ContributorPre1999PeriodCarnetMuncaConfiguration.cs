using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0311 — maps <see cref="ContributorPre1999PeriodCarnetMunca"/> to
/// <c>cnas.ContributorPre1999PeriodCarnetMunca</c>. No filtered unique index —
/// multiple historical periods may coexist (a citizen typically has 2-5 jobs in
/// their Carnet de muncă booklet). Index on ContributorId serves the per-citizen
/// listing endpoint.
/// </summary>
public sealed class ContributorPre1999PeriodCarnetMuncaConfiguration
    : AuditableEntityConfiguration<ContributorPre1999PeriodCarnetMunca>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContributorPre1999PeriodCarnetMunca> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContributorPre1999PeriodCarnetMunca");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.CarnetMuncaNumber).IsRequired().HasMaxLength(50);
        builder.Property(e => e.EmployerName).HasMaxLength(200);
        builder.Property(e => e.Position).HasMaxLength(200);
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => e.ContributorId);
        builder.HasIndex(e => e.PeriodStartDate);
    }
}
