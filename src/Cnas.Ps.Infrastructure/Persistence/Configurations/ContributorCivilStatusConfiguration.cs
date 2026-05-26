using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0311 — maps <see cref="ContributorCivilStatus"/> to
/// <c>cnas.ContributorCivilStatuses</c>. Filtered unique index enforces
/// single-current-row per Contributor.
/// </summary>
public sealed class ContributorCivilStatusConfiguration : AuditableEntityConfiguration<ContributorCivilStatus>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContributorCivilStatus> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContributorCivilStatuses");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => e.ContributorId);
        builder.HasIndex(e => e.ValidFromUtc);

        builder.HasIndex(e => e.ContributorId)
            .HasFilter("\"ValidToUtc\" IS NULL")
            .IsUnique()
            .HasDatabaseName("UX_ContributorCivilStatuses_CurrentRow");
    }
}
