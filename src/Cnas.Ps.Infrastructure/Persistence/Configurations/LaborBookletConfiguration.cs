using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0920 / TOR BP 2.3-A — maps <see cref="LaborBooklet"/> to
/// <c>cnas.LaborBooklets</c>. Single unique index on
/// <c>(InsuredPersonSolicitantId, CarnetMuncaNumber)</c> enforces the per-
/// citizen booklet-number uniqueness rule documented on the entity. A second
/// non-unique index on <c>(InsuredPersonSolicitantId, Status)</c> backs the
/// per-citizen pending-booklets listing endpoint.
/// </summary>
public sealed class LaborBookletConfiguration : AuditableEntityConfiguration<LaborBooklet>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<LaborBooklet> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LaborBooklets");

        builder.Property(e => e.InsuredPersonSolicitantId).IsRequired();
        builder.Property(e => e.CarnetMuncaNumber).IsRequired().HasMaxLength(32);
        builder.Property(e => e.IssuedDate);
        builder.Property(e => e.IssuingAuthority).HasMaxLength(200);
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.OcrExtractedJson);
        builder.Property(e => e.OcrConfidenceLevel).HasMaxLength(16);
        builder.Property(e => e.VerifierNotes).HasMaxLength(500);
        builder.Property(e => e.VerifiedByUserId);
        builder.Property(e => e.VerifiedAtUtc);
        builder.Property(e => e.RejectionReason).HasMaxLength(500);
        builder.Property(e => e.RejectedAtUtc);
        builder.Property(e => e.HasScannedCopy).IsRequired().HasDefaultValue(false);

        // Per-citizen unique booklet-number rule — two distinct citizens may
        // share a serial number (the paper archives are locally unique only)
        // but within ONE citizen the same booklet cannot be registered twice.
        builder.HasIndex(e => new { e.InsuredPersonSolicitantId, e.CarnetMuncaNumber })
            .IsUnique()
            .HasDatabaseName("UX_LaborBooklets_PerSolicitant_Number");

        // Per-citizen listing index — operators query "pending booklets for
        // citizen X" via this composite.
        builder.HasIndex(e => new { e.InsuredPersonSolicitantId, e.Status })
            .HasDatabaseName("IX_LaborBooklets_Solicitant_Status");
    }
}
