using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ReportTemplate"/> to <c>cnas.ReportTemplates</c>. The table is the
/// persistence half of the R0156 / CF 09.02 / FLEX 003 ad-hoc report builder surface:
/// one row per power-user-authored template.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>
///     <c>UNIQUE (Code)</c> — kebab-case identifier collisions are rejected at the DB
///     level so the idempotent-create semantics of the service layer cannot be
///     bypassed by a racing concurrent insert.
///   </description></item>
///   <item><description>
///     <c>(OwnerUserId)</c> — supports the own-rows half of
///     <c>ListAccessibleAsync</c>.
///   </description></item>
///   <item><description>
///     <c>(IsShared, Registry)</c> — supports the shared-rows half of
///     <c>ListAccessibleAsync</c>. Compound shape keeps the index slim because
///     <c>IsShared = true</c> is the minority case.
///   </description></item>
/// </list>
/// <para>
/// <b>Column widths and types.</b>
/// <list type="bullet">
///   <item><description><c>Code</c> — <c>varchar(128)</c> (validator caps at 128 chars).</description></item>
///   <item><description><c>Name</c> — <c>varchar(128)</c> (user-facing label).</description></item>
///   <item><description><c>Description</c> — <c>varchar(512)</c> (nullable).</description></item>
///   <item><description><c>Registry</c> — <c>varchar(32)</c> (registry codes are short PascalCase identifiers).</description></item>
///   <item><description><c>SelectedFieldsJson</c>, <c>FilterJson</c>, <c>OrderingJson</c> — <c>text</c> (opaque blobs; service caps each at 16 KiB).</description></item>
///   <item><description><c>GroupByField</c> — <c>varchar(64)</c> (nullable; matches QBE field-name length).</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ReportTemplateConfiguration : AuditableEntityConfiguration<ReportTemplate>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ReportTemplate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ReportTemplates");

        builder.Property(t => t.Code).IsRequired().HasMaxLength(128);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(128);
        builder.Property(t => t.Description).HasMaxLength(512);
        builder.Property(t => t.Registry).IsRequired().HasMaxLength(32);

        // Opaque JSON blobs — relaxed to text so the service caps (16 KiB) can be
        // tightened or loosened without a migration.
        builder.Property(t => t.SelectedFieldsJson).IsRequired().HasColumnType("text");
        builder.Property(t => t.FilterJson).IsRequired().HasColumnType("text");
        builder.Property(t => t.OrderingJson).IsRequired().HasColumnType("text");

        builder.Property(t => t.GroupByField).HasMaxLength(64);

        builder.Property(t => t.OwnerUserId).IsRequired();
        builder.Property(t => t.IsShared).IsRequired();

        // Unique on Code — DB-side safety net against a racing duplicate insert that
        // would otherwise produce two rows sharing the same kebab-case identifier.
        builder.HasIndex(t => t.Code).IsUnique();

        // Supports the own-rows half of ListAccessibleAsync — caller filters by owner.
        builder.HasIndex(t => t.OwnerUserId);

        // Supports the shared-rows half — IsShared=true narrows the row set
        // dramatically, and the Registry equality filter narrows it further.
        builder.HasIndex(t => new { t.IsShared, t.Registry });
    }
}
