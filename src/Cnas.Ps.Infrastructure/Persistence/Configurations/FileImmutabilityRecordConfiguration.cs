using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0137 — maps <see cref="FileImmutabilityRecord"/> to <c>cnas.FileImmutabilityRecords</c>.
/// </summary>
/// <remarks>
/// <para>
/// Constraints:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       Partial unique index on <c>(Bucket, ObjectKey)</c> covering only
///       <c>IsActive=true</c> rows so <c>MarkImmutableAsync</c> can detect "already marked"
///       deterministically. Soft-deleted rows are tolerated because the application's
///       cross-cutting "Soft Deletes" rule never hard-removes business-meaningful rows.
///     </description>
///   </item>
///   <item>
///     <description>
///       Column lengths: <c>Bucket</c> 128 (matches MinIO S3-style bucket cap), <c>ObjectKey</c>
///       512 (covers the date-partitioned <c>yyyy/MM/dd/{guid}</c> layout plus headroom),
///       <c>Reason</c> 256 (operator-visible free-form rationale).
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class FileImmutabilityRecordConfiguration : AuditableEntityConfiguration<FileImmutabilityRecord>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<FileImmutabilityRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("FileImmutabilityRecords");

        builder.Property(r => r.Bucket).IsRequired().HasMaxLength(128);
        builder.Property(r => r.ObjectKey).IsRequired().HasMaxLength(512);
        builder.Property(r => r.MarkedAtUtc).IsRequired();
        builder.Property(r => r.MarkedByUserId);
        builder.Property(r => r.Reason).HasMaxLength(256);

        // Partial unique index over (Bucket, ObjectKey) covering ONLY IsActive=true rows.
        // The EF model declares the partial filter via HasFilter so Postgres builds a
        // partial B-tree; in-memory provider ignores the filter, which is fine because
        // the application code never relies on duplicate-key behaviour to detect a
        // double-mark — it uses an explicit existence query.
        builder.HasIndex(r => new { r.Bucket, r.ObjectKey })
               .IsUnique()
               .HasFilter("\"IsActive\" = true");
    }
}
