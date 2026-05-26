using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0227 / TOR UI 014 — maps <see cref="AttachmentRecord"/> to
/// <c>cnas.AttachmentRecords</c>. The table is the persistence half of the reusable
/// file-attachment widget — one row per uploaded attachment, with the binary stored
/// out-of-band in the configured blob backend via the opaque
/// <see cref="AttachmentRecord.StorageKey"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>(OwnerEntityType, OwnerEntityId)</c> — supports the listing query path
///       ("list every attachment for owner X"). The owner column pair is consulted on
///       every list / dedup / download call.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>UNIQUE (OwnerEntityType, OwnerEntityId, Sha256Hex) WHERE IsActive = true</c>
///       — enforces the per-owner dedup contract documented on the entity. The filtered
///       predicate keeps the index slim: soft-deleted rows are excluded so a re-upload
///       of a previously-deleted byte-identical file mints a fresh row instead of
///       colliding.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Column widths and types.</b>
/// <list type="bullet">
///   <item><description><c>OwnerEntityType</c> — <c>varchar(64)</c>.</description></item>
///   <item><description><c>FileName</c> — <c>varchar(255)</c>.</description></item>
///   <item><description><c>ContentType</c> — <c>varchar(128)</c>.</description></item>
///   <item><description><c>StorageKey</c> — <c>varchar(512)</c>.</description></item>
///   <item><description><c>Sha256Hex</c> — <c>varchar(64)</c> (lowercase hex digest).</description></item>
///   <item><description><c>Category</c> — <c>int</c> via the default enum converter; the
///     numeric values on <see cref="AttachmentCategory"/> are part of the persistence
///     contract.</description></item>
///   <item><description><c>SensitivityLevel</c> — <c>int</c>; mirrors
///     <c>Cnas.Ps.Contracts.Security.SensitivityLabel</c> (Public=0..Restricted=3).</description></item>
///   <item><description><c>Description</c> — <c>varchar(500)</c>; nullable.</description></item>
/// </list>
/// </para>
/// <para>
/// No FK constraint is declared against any owner table because the
/// <see cref="AttachmentRecord.OwnerEntityType"/> column is polymorphic. The
/// application-layer service validates the owner-type string against the frozen
/// allow-list constants before persisting.
/// </para>
/// </remarks>
public sealed class AttachmentRecordConfiguration : AuditableEntityConfiguration<AttachmentRecord>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<AttachmentRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AttachmentRecords");

        builder.Property(a => a.OwnerEntityType).IsRequired().HasMaxLength(64);
        builder.Property(a => a.OwnerEntityId).IsRequired();
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(255);
        builder.Property(a => a.ContentType).IsRequired().HasMaxLength(128);
        builder.Property(a => a.SizeBytes).IsRequired();
        builder.Property(a => a.StorageKey).IsRequired().HasMaxLength(512);
        builder.Property(a => a.Sha256Hex).IsRequired().HasMaxLength(64);

        builder.Property(a => a.Category)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(a => a.SensitivityLevel).IsRequired();
        builder.Property(a => a.Description).HasMaxLength(500);
        builder.Property(a => a.UploadedByUserId).IsRequired();
        builder.Property(a => a.UploadedUtc).IsRequired();
        builder.Property(a => a.IsArchived).IsRequired();

        // Owner lookup — supports listing and dedup-lookup paths.
        builder.HasIndex(a => new { a.OwnerEntityType, a.OwnerEntityId });

        // Per-owner content dedup — the same byte-identical file uploaded twice under
        // one owner reuses the existing row. Filtered on IsActive so a soft-deleted
        // attachment does not block a fresh upload of an identical replacement.
        builder.HasIndex(a => new { a.OwnerEntityType, a.OwnerEntityId, a.Sha256Hex })
            .IsUnique()
            .HasFilter("\"IsActive\" = true");
    }
}
