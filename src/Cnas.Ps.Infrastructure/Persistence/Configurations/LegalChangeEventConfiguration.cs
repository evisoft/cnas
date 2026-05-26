using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1503 / TOR §3.7-D — maps <see cref="LegalChangeEvent"/> to
/// <c>cnas.LegalChangeEvents</c>. Unique index on
/// <see cref="LegalChangeEvent.Code"/>; dashboard index on
/// <c>(Status, EffectiveFrom)</c> for the operator-list browse.
/// </summary>
public sealed class LegalChangeEventConfiguration : AuditableEntityConfiguration<LegalChangeEvent>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<LegalChangeEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LegalChangeEvents");

        builder.Property(e => e.Code).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Title).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasMaxLength(2000);
        builder.Property(e => e.EffectiveFrom).IsRequired();

        builder.Property(e => e.Scope)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        // Snapshot of benefit-type names — Postgres text[] (mirrors UserGroup.Roles convention).
        builder.Property(e => e.BenefitTypesInScope)
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(e => e.ChangePayloadJson).HasMaxLength(16384);
        builder.Property(e => e.RegisteredByUserId).IsRequired();
        builder.Property(e => e.CancellationReason).HasMaxLength(500);

        builder.HasIndex(e => e.Code)
            .IsUnique()
            .HasDatabaseName("UX_LegalChangeEvents_Code");

        builder.HasIndex(e => new { e.Status, e.EffectiveFrom })
            .HasDatabaseName("IX_LegalChangeEvents_Status_EffectiveFrom");
    }
}
