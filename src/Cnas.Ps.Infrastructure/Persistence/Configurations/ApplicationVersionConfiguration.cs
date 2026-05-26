using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0321 / R0224 / UI 008 — maps <see cref="ApplicationVersion"/> to
/// <c>cnas.ApplicationVersions</c>. The table is the persistence half of the
/// auto-save / draft-history surface: one row per <see cref="ServiceApplication"/>
/// save (auto or manual), append-only with a single <c>IsCurrent</c> row pinning the
/// latest revision.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE (ServiceApplicationId, VersionNumber)</c> — natural key. The service
///       advances <see cref="ApplicationVersion.VersionNumber"/> by one on each save;
///       the unique index is the DB-side safety net against a racing concurrent insert
///       that would otherwise mint two rows with the same version number for one
///       application.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(ServiceApplicationId, IsCurrent) WHERE IsCurrent = true</c> — partial
///       unique index. Enforces the "exactly one current row per application" invariant
///       at the DB layer. The partial predicate keeps the index slim (one entry per
///       application regardless of how many historical revisions accumulate).
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(ServiceApplicationId, CreatedAtUtc DESC)</c> — supports the listing query
///       (most recent first). EF Core's default ascending column ordering would still
///       be usable by the planner, but emitting the explicit DESC sort matches the
///       query shape exactly.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Column types.</b>
/// <list type="bullet">
///   <item><description><c>FormDataJson</c> — <c>text</c> (round-tripped verbatim;
///     service caps at <c>MaxFormDataKb</c> KB).</description></item>
///   <item><description><c>Source</c> — stored as <c>int</c> via the default enum
///     converter; the numeric values on <see cref="ApplicationVersionSource"/> are part
///     of the persistence contract.</description></item>
///   <item><description><c>Note</c> — <c>varchar(1000)</c>; nullable.</description></item>
/// </list>
/// </para>
/// <para>
/// No foreign-key constraint is declared against <c>UserProfiles</c> for
/// <see cref="ApplicationVersion.CreatedByUserId"/> — we mirror the same pattern used
/// elsewhere (e.g. <see cref="SavedSearch.OwnerUserId"/>) because the application enforces
/// the FK semantics and a hard FK would fight GDPR right-to-erasure cascades on the user
/// side. A FK against <see cref="ServiceApplication"/> IS declared because version rows
/// without a parent application have no meaning.
/// </para>
/// </remarks>
public sealed class ApplicationVersionConfiguration : AuditableEntityConfiguration<ApplicationVersion>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ApplicationVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ApplicationVersions");

        builder.Property(v => v.ServiceApplicationId).IsRequired();
        builder.Property(v => v.VersionNumber).IsRequired();

        // text — service layer caps the payload at MaxFormDataKb KB; relaxing the cap
        // later does not require a migration. Opaque to EF Core — no value converter.
        builder.Property(v => v.FormDataJson).IsRequired().HasColumnType("text");

        builder.Property(v => v.CreatedByUserId).IsRequired();

        // Stored as int — the numeric values of ApplicationVersionSource are part of the
        // persistence contract per the enum's XML doc.
        builder.Property(v => v.Source)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(v => v.Note).HasMaxLength(1000);

        builder.Property(v => v.IsCurrent).IsRequired();

        // FK to the owning application. Cascade delete is NOT applied because applications
        // are soft-deleted (IsActive = false) rather than hard-deleted; the version rows
        // remain queryable alongside the application's soft-deleted state.
        builder.HasOne<ServiceApplication>()
            .WithMany()
            .HasForeignKey(v => v.ServiceApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Natural key — inserting a duplicate (ServiceApplicationId, VersionNumber) is a
        // programming error in the service; the unique constraint converts it into a
        // deterministic DbUpdateException.
        builder.HasIndex(v => new { v.ServiceApplicationId, v.VersionNumber }).IsUnique();

        // Partial unique index — exactly one current row per application at any time.
        // The HasFilter clause uses Postgres quoting because the column name is PascalCase.
        builder.HasIndex(v => new { v.ServiceApplicationId, v.IsCurrent })
            .IsUnique()
            .HasFilter("\"IsCurrent\" = true");

        // Listing query support (most recent first per application). The composite shape
        // matches the WHERE + ORDER BY exactly.
        builder.HasIndex(v => new { v.ServiceApplicationId, v.CreatedAtUtc })
            .IsDescending(false, true);
    }
}
