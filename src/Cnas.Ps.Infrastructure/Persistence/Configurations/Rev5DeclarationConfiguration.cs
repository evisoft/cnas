using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0910 / TOR BP 2.2-A — maps <see cref="Rev5Declaration"/> to
/// <c>cnas.Rev5Declarations</c>. A unique index on
/// <c>(FilingContributorId, ReportingMonth, ReferenceNumber)</c> enforces the
/// natural-key uniqueness rule documented on the entity, and listing indexes
/// support per-employer history queries.
/// </summary>
public sealed class Rev5DeclarationConfiguration : AuditableEntityConfiguration<Rev5Declaration>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Rev5Declaration> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Rev5Declarations");

        builder.Property(e => e.FilingContributorId).IsRequired();
        builder.Property(e => e.ReportingMonth).IsRequired();
        builder.Property(e => e.FiledAtUtc).IsRequired();
        builder.Property(e => e.ReferenceNumber).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.TotalDeclaredAmount).HasPrecision(18, 2);
        builder.Property(e => e.RowCount).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(500);

        // Natural-key uniqueness: an employer cannot register the same
        // REV-5 reference twice for the same reporting month.
        builder.HasIndex(e => new { e.FilingContributorId, e.ReportingMonth, e.ReferenceNumber })
            .IsUnique()
            .HasDatabaseName("UX_Rev5Declarations_NaturalKey");

        // Listing index — operator queries that filter by employer ordered by
        // ReportingMonth DESC.
        builder.HasIndex(e => new { e.FilingContributorId, e.ReportingMonth })
            .HasDatabaseName("IX_Rev5Declarations_Contributor_Month");
    }
}
