using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0311 — maps <see cref="ContributorActivityPeriod"/> to
/// <c>cnas.ContributorActivityPeriods</c>. Deliberately NO filtered unique index — a
/// citizen may have multiple concurrent employment periods. The base
/// <see cref="AuditableEntityConfiguration{T}"/> still wires the IsActive + CreatedAtUtc
/// indexes for soft-delete and audit listing.
/// </summary>
public sealed class ContributorActivityPeriodConfiguration : AuditableEntityConfiguration<ContributorActivityPeriod>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContributorActivityPeriod> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContributorActivityPeriods");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.EmployerCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Position).IsRequired().HasMaxLength(200);
        builder.Property(e => e.MonthlySalary).HasPrecision(18, 2);
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => e.ContributorId);
        builder.HasIndex(e => e.ValidFromUtc);
    }
}
