using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0201 / TOR CF 20.02 — maps <see cref="KpiSnapshot"/> to the
/// <c>cnas.KpiSnapshots</c> table. One row per
/// (<see cref="KpiSnapshot.SnapshotDate"/>, <see cref="KpiSnapshot.KpiCode"/>,
/// <see cref="KpiSnapshot.Dimension1"/>, <see cref="KpiSnapshot.Dimension2"/>)
/// tuple, enforced via a composite unique index. The two optional dimension
/// columns default to the empty string (NEVER <c>null</c>) so SQL's
/// <c>NULL ≠ NULL</c> uniqueness rule does not let duplicates slip through.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UX_KpiSnapshots_NaturalKey</c> — composite unique on
///       (<c>SnapshotDate</c>, <c>KpiCode</c>, <c>Dimension1</c>,
///       <c>Dimension2</c>) — protects the snapshot upsert idempotency
///       contract.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>IX_KpiSnapshots_SnapshotDateDesc_KpiCode</c> — supports the
///       dashboard read path "newest first, filtered by KPI code".
///     </description>
///   </item>
///   <item>
///     <description>
///       Column widths and types: <c>KpiCode</c> = <c>varchar(64)</c>;
///       <c>Dimension1</c> / <c>Dimension2</c> = <c>varchar(64)</c> (default
///       empty string); <c>ValueUnit</c> = <c>varchar(16)</c>; <c>Value</c>
///       = <c>numeric(20, 4)</c> — four fractional digits is enough for
///       percentages, ratios, and average-hours rounded to the quarter-hour.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class KpiSnapshotConfiguration : AuditableEntityConfiguration<KpiSnapshot>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<KpiSnapshot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("KpiSnapshots");

        builder.Property(s => s.SnapshotDate).IsRequired();
        builder.Property(s => s.KpiCode).IsRequired().HasMaxLength(64);
        builder.Property(s => s.Value)
            .IsRequired()
            .HasColumnType("numeric(20, 4)");
        builder.Property(s => s.ValueUnit).IsRequired().HasMaxLength(16);

        // Dimensions default to the empty string at both the CLR and DB layer
        // so the unique index over the natural key behaves as documented (SQL
        // treats NULL ≠ NULL, which would defeat the uniqueness guarantee).
        builder.Property(s => s.Dimension1)
            .IsRequired()
            .HasMaxLength(64)
            .HasDefaultValue(string.Empty);
        builder.Property(s => s.Dimension2)
            .IsRequired()
            .HasMaxLength(64)
            .HasDefaultValue(string.Empty);

        builder.HasIndex(s => new { s.SnapshotDate, s.KpiCode, s.Dimension1, s.Dimension2 })
            .IsUnique()
            .HasDatabaseName("UX_KpiSnapshots_NaturalKey");

        builder.HasIndex(s => new { s.SnapshotDate, s.KpiCode })
            .IsDescending(true, false)
            .HasDatabaseName("IX_KpiSnapshots_SnapshotDateDesc_KpiCode");
    }
}
