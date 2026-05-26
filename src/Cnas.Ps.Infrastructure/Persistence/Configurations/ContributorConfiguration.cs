using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Contributor"/> to the <c>cnas.Contributors</c> table.</summary>
public sealed class ContributorConfiguration : AuditableEntityConfiguration<Contributor>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Contributor> builder)
    {
        builder.ToTable("Contributors");

        // Idno is encrypted at rest — widened from VARCHAR(13) to VARCHAR(128) to hold the
        // v1: envelope. See Solicitant.NationalId for the size rationale.
        builder.Property(c => c.Idno).IsRequired().HasMaxLength(128);
        builder.Property(c => c.Denumire).IsRequired().HasMaxLength(512);
        builder.Property(c => c.CfojCode).HasMaxLength(32);
        // R0805 / Annex 1 §8.1.1.6 — new classifier column landed in iter-138.
        builder.Property(c => c.CfpCode).HasMaxLength(32);
        builder.Property(c => c.CaemCode).HasMaxLength(32);
        builder.Property(c => c.UpstreamRsudId).HasMaxLength(64);

        // Shadow column for equality lookups on the encrypted Idno. Base64(HMAC-SHA256) = 44.
        builder.Property(c => c.IdnoHash).IsRequired().HasMaxLength(44);

        // R0305 / BP 1.3 — lifecycle columns. Deactivation reason is free-form text;
        // CnasBranchCode is the natural code of the regional branch (no FK constraint —
        // operators may add branches independently of contributors).
        builder.Property(c => c.DeactivationReason).HasMaxLength(500);
        builder.Property(c => c.CnasBranchCode).HasMaxLength(32);

        // Unique index moves to the hash column — see SolicitantConfiguration for rationale.
        builder.HasIndex(c => c.IdnoHash).IsUnique();
        builder.HasIndex(c => c.IsInsolvent);

        // R0305 — indexes supporting the new lifecycle queries:
        //  * IsDeactivated drives the "show only active contributors" registry filter.
        //  * MergedIntoContributorId drives the "list duplicates merged into X" lookup
        //    (BP 1.5 reverse navigation for investigators).
        //  * CnasBranchCode drives the "list contributors at branch X" filter used by
        //    the bulk reassignment operation (BP 1.8).
        builder.HasIndex(c => c.IsDeactivated);
        builder.HasIndex(c => c.MergedIntoContributorId);
        builder.HasIndex(c => c.CnasBranchCode);
    }
}
