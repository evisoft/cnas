using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0153 / TOR CF 19.05 — maps <see cref="ContributorPeriodProjection"/> to
/// <c>cnas.ContributorPeriodProjections</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UX_ContributorPeriodProjections_NaturalKey</c> — composite unique on
///       (<c>ContributorId</c>, <c>PeriodStartUtc</c>) — exactly one row per
///       contributor per slice start.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>IX_ContributorPeriodProjections_Contributor_PeriodStartDesc</c> —
///       supports the "latest slice for contributor X" lookup the dashboard
///       tiles emit.
///     </description>
///   </item>
///   <item>
///     <description>
///       Column widths: <c>CivilStatus</c> = <c>varchar(32)</c>;
///       <c>CurrentEmployerCode</c> = <c>varchar(64)</c>;
///       <c>AddressCity</c> / <c>AddressRegion</c> = <c>varchar(200)</c>;
///       <c>AddressCountry</c> = <c>char(2)</c>; <c>PhoneE164</c> =
///       <c>varchar(32)</c>; <c>Email</c> = <c>varchar(254)</c>;
///       <c>MonthlySalary</c> = <c>numeric(18, 4)</c>.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class ContributorPeriodProjectionConfiguration
    : AuditableEntityConfiguration<ContributorPeriodProjection>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContributorPeriodProjection> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContributorPeriodProjections");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.PeriodStartUtc).IsRequired();
        builder.Property(e => e.PeriodEndUtc).IsRequired();
        builder.Property(e => e.ProjectedAtUtc).IsRequired();

        builder.Property(e => e.CivilStatus).HasMaxLength(32);
        builder.Property(e => e.CurrentEmployerCode).HasMaxLength(64);
        builder.Property(e => e.MonthlySalary).HasColumnType("numeric(18, 4)");
        builder.Property(e => e.AddressCity).HasMaxLength(200);
        builder.Property(e => e.AddressRegion).HasMaxLength(200);
        builder.Property(e => e.AddressCountry).HasMaxLength(2);
        builder.Property(e => e.PhoneE164).HasMaxLength(32);
        builder.Property(e => e.Email).HasMaxLength(254);

        builder.HasIndex(e => new { e.ContributorId, e.PeriodStartUtc })
            .IsUnique()
            .HasDatabaseName("UX_ContributorPeriodProjections_NaturalKey");

        builder.HasIndex(e => new { e.ContributorId, e.PeriodStartUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_ContributorPeriodProjections_Contributor_PeriodStartDesc");
    }
}
