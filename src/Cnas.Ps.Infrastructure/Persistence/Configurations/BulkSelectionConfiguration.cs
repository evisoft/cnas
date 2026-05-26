using System.Text.Json;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — maps <see cref="BulkSelection"/> to
/// <c>cnas.BulkSelections</c>. The table is the persistence half of the bulk-selection
/// surface: one row per <c>POST /api/bulk-actions/selections</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>(OwnerUserId)</c> — supports the per-owner list path (used by the
///       service's <c>GetAsync</c> ownership check and the cleanup job's
///       per-owner sweeps if added later).
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(IsConsumed, ExpiresAtUtc)</c> — supports the cleanup job's predicate
///       "find rows that are consumed OR expired past the grace window". The
///       compound shape keeps the index slim because the typical lifetime of a
///       selection is short.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Column widths and types.</b>
/// <list type="bullet">
///   <item><description><c>Registry</c> — <c>varchar(32)</c>.</description></item>
///   <item><description><c>FilterJson</c> — <c>text</c> (round-tripped verbatim; service caps at 8 KB).</description></item>
///   <item><description><c>ExplicitIncludeIds</c>/<c>ExplicitExcludeIds</c> — <c>jsonb</c> via
///   an EF Core value converter that serialises the <see cref="List{T}"/> to a JSON array of
///   <see cref="long"/>. Defaults to an empty array.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class BulkSelectionConfiguration : AuditableEntityConfiguration<BulkSelection>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<BulkSelection> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BulkSelections");

        builder.Property(s => s.Registry).IsRequired().HasMaxLength(32);
        builder.Property(s => s.OwnerUserId).IsRequired();
        builder.Property(s => s.FilterJson).IsRequired().HasColumnType("text");

        builder.Property(s => s.ResolvedCount).IsRequired();
        builder.Property(s => s.ExpiresAtUtc).IsRequired();
        builder.Property(s => s.IsConsumed).IsRequired();

        // Persist the include / exclude lists as jsonb. The converter serialises a
        // List<long> to its JSON form and back; the comparer is value-based so EF
        // change tracking detects list mutations (replace + clear).
        var jsonOpts = new JsonSerializerOptions { WriteIndented = false };
        var listComparer = new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<long>>(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            v => v == null ? 0 : v.Aggregate(0, (acc, n) => HashCode.Combine(acc, n)),
            v => v == null ? new List<long>() : v.ToList());

        builder.Property(s => s.ExplicitIncludeIds)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new List<long>(), jsonOpts),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<long>()
                    : JsonSerializer.Deserialize<List<long>>(v, jsonOpts) ?? new List<long>())
            .Metadata.SetValueComparer(listComparer);

        builder.Property(s => s.ExplicitExcludeIds)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v ?? new List<long>(), jsonOpts),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<long>()
                    : JsonSerializer.Deserialize<List<long>>(v, jsonOpts) ?? new List<long>())
            .Metadata.SetValueComparer(listComparer);

        builder.HasIndex(s => s.OwnerUserId);
        builder.HasIndex(s => new { s.IsConsumed, s.ExpiresAtUtc });
    }
}
