using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Dossier"/> to <c>cnas.Dossiers</c>.</summary>
public sealed class DossierConfiguration : AuditableEntityConfiguration<Dossier>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Dossier> builder)
    {
        builder.ToTable("Dossiers");

        builder.Property(d => d.DossierNumber).IsRequired().HasMaxLength(64);
        builder.Property(d => d.ApplicationId).IsRequired();
        // Monetary amount uses numeric(18,2): 16 integer digits ample for MDL benefits
        // (largest theoretical value far exceeds any realistic single-dossier award), 2 fractional digits cover bani.
        builder.Property(d => d.ComputedAmountMdl).HasColumnType("numeric(18,2)");

        builder.HasOne(d => d.Application)
            .WithMany()
            .HasForeignKey(d => d.ApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => d.DossierNumber).IsUnique();
        builder.HasIndex(d => d.AssignedExaminerId);
        builder.HasIndex(d => d.ApproverId);
    }
}
