using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0910 / TOR BP 2.2-A — maps <see cref="Rev5DeclarationRow"/> to
/// <c>cnas.Rev5DeclarationRows</c>. A composite unique index on
/// <c>(Rev5DeclarationId, InsuredPersonNationalIdHash)</c> enforces the
/// natural-key rule documented on the entity; a cascade-delete FK ties the
/// child row's lifecycle to the parent header.
/// </summary>
public sealed class Rev5DeclarationRowConfiguration : AuditableEntityConfiguration<Rev5DeclarationRow>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Rev5DeclarationRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Rev5DeclarationRows");

        builder.Property(e => e.Rev5DeclarationId).IsRequired();
        builder.Property(e => e.InsuredPersonNationalIdHash).IsRequired().HasMaxLength(128);
        builder.Property(e => e.ContributionBaseAmount).HasPrecision(18, 2);
        builder.Property(e => e.ContributionAmount).HasPrecision(18, 2);
        builder.Property(e => e.DaysWorked);
        builder.Property(e => e.PositionCode).HasMaxLength(64);

        // Cascade delete from Rev5Declaration → Rev5DeclarationRow. Modeled as
        // a plain FK without a navigation property — the service layer loads
        // child rows via an explicit Where clause.
        builder.HasOne<Rev5Declaration>()
            .WithMany()
            .HasForeignKey(e => e.Rev5DeclarationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Natural-key uniqueness: an employee may appear at most once per
        // REV-5 declaration.
        builder.HasIndex(e => new { e.Rev5DeclarationId, e.InsuredPersonNationalIdHash })
            .IsUnique()
            .HasDatabaseName("UX_Rev5DeclarationRows_NaturalKey");

        // Cross-declaration lookup by IDNP hash (used by the per-citizen
        // contribution-history queries — projected through
        // PersonalAccountEntry, but a direct index here speeds back-fill jobs).
        builder.HasIndex(e => e.InsuredPersonNationalIdHash)
            .HasDatabaseName("IX_Rev5DeclarationRows_NationalIdHash");
    }
}
