using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0516 / TOR CF 02.04 — maps <see cref="PersonalAccountEntry"/> to
/// <c>cnas.PersonalAccountEntries</c>. A composite unique index on
/// <c>(PersonalAccountId, Year, Month, SourceCode)</c> enforces the
/// natural-key rule documented on the entity. The plain bigint
/// foreign-key column references the owning <see cref="PersonalAccount"/>
/// row by surrogate id; no navigation property is configured because the
/// extract service loads entries via an explicit <c>WHERE</c> clause and
/// has no need for lazy / eager loading semantics.
/// </summary>
public sealed class PersonalAccountEntryConfiguration : AuditableEntityConfiguration<PersonalAccountEntry>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PersonalAccountEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PersonalAccountEntries");

        builder.Property(e => e.PersonalAccountId).IsRequired();
        builder.Property(e => e.Year).IsRequired();
        builder.Property(e => e.Month).IsRequired();
        builder.Property(e => e.SourceCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ContributionBaseAmount).HasPrecision(18, 2);
        builder.Property(e => e.ContributionPaidAmount).HasPrecision(18, 2);

        // Composite unique index — see entity remarks for the natural-key
        // rationale. Soft-deleted rows still occupy their slot; the application
        // layer reactivates rather than re-inserts when fixing a clerical
        // mistake against a previously-deleted entry.
        builder.HasIndex(e => new { e.PersonalAccountId, e.Year, e.Month, e.SourceCode })
            .IsUnique()
            .HasDatabaseName("UX_PersonalAccountEntries_NaturalKey");

        // Secondary index — every extract query filters by the owning account
        // and orders by Year DESC, Month ASC. The composite UX above already
        // covers this access path; an explicit index on the FK alone is kept
        // for the per-account count() projections used by ops dashboards.
        builder.HasIndex(e => e.PersonalAccountId);
    }
}
