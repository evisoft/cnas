using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="InsuredPerson"/> to <c>cnas.InsuredPersons</c>.</summary>
public sealed class InsuredPersonConfiguration : AuditableEntityConfiguration<InsuredPerson>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<InsuredPerson> builder)
    {
        builder.ToTable("InsuredPersons");

        // Idnp is encrypted at rest — widened from VARCHAR(13) to VARCHAR(128) to hold the
        // v1: envelope. See Solicitant.NationalId for the size rationale.
        builder.Property(p => p.Idnp).IsRequired().HasMaxLength(128);
        builder.Property(p => p.LastName).IsRequired().HasMaxLength(128);
        builder.Property(p => p.FirstName).IsRequired().HasMaxLength(128);
        builder.Property(p => p.Patronymic).HasMaxLength(128);

        // Shadow column for equality lookups on the encrypted Idnp — also backs the load-bearing
        // Solicitant→InsuredPerson join used by Annex 6f (RPT-CASES-BY-AGE-GROUP). Base64(HMAC-SHA256) = 44.
        builder.Property(p => p.IdnpHash).IsRequired().HasMaxLength(44);

        // Unique index moves to the hash column — see SolicitantConfiguration for rationale.
        builder.HasIndex(p => p.IdnpHash).IsUnique();
        builder.HasIndex(p => p.IsDeceased);
        builder.HasIndex(p => p.BirthDate);
    }
}
