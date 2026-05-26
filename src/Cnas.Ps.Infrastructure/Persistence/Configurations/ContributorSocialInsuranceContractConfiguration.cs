using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0311 — maps <see cref="ContributorSocialInsuranceContract"/> to
/// <c>cnas.ContributorSocialInsuranceContracts</c>. Filtered unique index enforces
/// single-current-row per Contributor.
/// </summary>
public sealed class ContributorSocialInsuranceContractConfiguration
    : AuditableEntityConfiguration<ContributorSocialInsuranceContract>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContributorSocialInsuranceContract> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContributorSocialInsuranceContracts");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.ContractNumber).IsRequired().HasMaxLength(50);
        builder.Property(e => e.MonthlyContributionAmount).HasPrecision(18, 2);
        builder.Property(e => e.CounterpartyName).HasMaxLength(200);
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => e.ContributorId);
        builder.HasIndex(e => e.ValidFromUtc);

        builder.HasIndex(e => e.ContributorId)
            .HasFilter("\"ValidToUtc\" IS NULL")
            .IsUnique()
            .HasDatabaseName("UX_ContributorSocialInsuranceContracts_CurrentRow");
    }
}
