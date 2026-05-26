using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="SavedSearch"/> to <c>cnas.SavedSearches</c>. The table is the
/// persistence half of the R0165 / CF 03.06 "saved searches" surface: one row per saved
/// query, owned by the creating user and optionally published to colleagues via
/// <see cref="SavedSearch.IsShared"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE (OwnerUserId, Registry, Name)</c> — the natural key. Enforces "one
///       saved query per (owner, registry, name)" at the DB level so the idempotent-create
///       semantics of the service layer cannot be bypassed by a racing concurrent insert.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(IsShared, Registry)</c> — supports the shared-rows half of the list query
///       (caller is not the owner). The compound shape keeps the index slim because
///       <c>IsShared = true</c> is the minority case in any normal usage pattern.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(OwnerUserId)</c> — supports the own-rows half of the list query. The
///       partner index to the natural key — kept narrow so the planner picks it for
///       lookups that filter on the owner alone.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Column widths and types.</b>
/// <list type="bullet">
///   <item><description><c>Registry</c> — <c>varchar(32)</c> (registry codes are short kebab/PascalCase identifiers).</description></item>
///   <item><description><c>Name</c> — <c>varchar(128)</c> (user-facing label; ample for any reasonable UI string).</description></item>
///   <item><description><c>FilterJson</c> — <c>text</c> (round-tripped verbatim; service caps at 8192 bytes).</description></item>
/// </list>
/// </para>
/// <para>
/// The standard <c>(IsActive)</c> and <c>(CreatedAtUtc)</c> indexes inherited from
/// <see cref="AuditableEntityConfiguration{TEntity}"/> are also created so the
/// soft-delete-aware list paths and audit timeline projections remain cheap.
/// </para>
/// </remarks>
public sealed class SavedSearchConfiguration : AuditableEntityConfiguration<SavedSearch>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SavedSearch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SavedSearches");

        builder.Property(s => s.OwnerUserId).IsRequired();

        // Registry codes are short stable identifiers (Contributors, Insured, ...). 32 is
        // ample and gives the planner a known upper bound to plan the (IsShared, Registry)
        // index against.
        builder.Property(s => s.Registry).IsRequired().HasMaxLength(32);

        // 128 chars matches the service-layer cap so over-long names surface as a clean
        // ValidationFailed Result rather than a database constraint violation.
        builder.Property(s => s.Name).IsRequired().HasMaxLength(128);

        // text — service layer caps the JSON at 8192 bytes; relaxing the cap later does
        // not require a migration. The column is opaque to EF Core (no value converter)
        // because the JSON is round-tripped verbatim.
        builder.Property(s => s.FilterJson).IsRequired().HasColumnType("text");

        builder.Property(s => s.IsShared).IsRequired();

        // R0524 — sharing scope (Private/Shared/Group). Persisted as int (the enum's
        // numeric value); the wire surface renders the stable ToString() name. Default
        // value Private is set by the entity field initialiser; EF reads/writes the
        // column directly.
        builder.Property(s => s.SharingScope).IsRequired().HasConversion<int>();

        // R0524 — group code companion field. 64 chars matches the validator's
        // group-code regex upper bound (1-64 chars excluding the leading anchor) so
        // over-long values surface as ValidationFailed rather than a column-size error.
        builder.Property(s => s.SharedWithGroupCode).HasMaxLength(64);

        // Natural key — the service treats a duplicate create on the same triple as an
        // idempotent return of the existing row's Sqid. The unique constraint is the
        // DB-side safety net against a racing duplicate insert (two requests landing in
        // separate transactions before either persists).
        builder.HasIndex(s => new { s.OwnerUserId, s.Registry, s.Name }).IsUnique();

        // Supports the list-shared half of ListAsync — non-owner callers reading rows
        // their colleagues have published. Compound shape because the registry filter is
        // applied alongside IsShared on every list call.
        builder.HasIndex(s => new { s.IsShared, s.Registry });

        // Supports the list-mine half of ListAsync and the per-owner cap check on create.
        builder.HasIndex(s => s.OwnerUserId);

        // R0524 — supports the discovery query in ListAccessibleAsync (filter by
        // SharingScope = Shared / Group + Registry). Compound shape so the planner can
        // narrow on both columns in a single index lookup; the scope column is the
        // leading axis because the query always filters scope as a literal in-list.
        builder.HasIndex(s => new { s.SharingScope, s.Registry });
    }
}
