using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0133 / TOR CF 17.16 — Maps <see cref="TemplateVariant"/> to
/// <c>cnas.TemplateVariants</c>. Owns the natural-key uniqueness rule
/// <c>(TemplateId, Language)</c> so the renderer's per-locale lookup yields at most
/// one row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexes declared here</b> (soft-delete + audit timestamps are contributed by
/// <see cref="AuditableEntityConfiguration{TEntity}"/>):
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE (TemplateId, Language)</c> — natural key. Attempting to upsert two
///       variants for the same template-language pair surfaces as a deterministic
///       <c>DbUpdateException</c>.
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Column widths.</b>
/// <list type="bullet">
///   <item><description><c>Language</c> — <c>varchar(8)</c> (ISO 639-1 codes are 2 chars; 8 leaves room for ICU-style locale extensions).</description></item>
///   <item><description><c>SubjectOrTitle</c> — <c>varchar(200)</c> (matches the FluentValidation cap).</description></item>
///   <item><description><c>Body</c> — <c>text</c> (capped at the service layer to 100,000 chars).</description></item>
///   <item><description><c>RenderedDocxBytes</c> — <c>bytea</c> nullable.</description></item>
///   <item><description><c>DocxFileName</c> — <c>varchar(256)</c> nullable.</description></item>
///   <item><description><c>TranslatorNote</c> — <c>text</c> nullable.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class TemplateVariantConfiguration : AuditableEntityConfiguration<TemplateVariant>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<TemplateVariant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TemplateVariants");

        builder.Property(v => v.TemplateId).IsRequired();
        builder.Property(v => v.Language).IsRequired().HasMaxLength(8);
        builder.Property(v => v.SubjectOrTitle).IsRequired().HasMaxLength(200);
        builder.Property(v => v.Body).IsRequired().HasColumnType("text");
        builder.Property(v => v.RenderedDocxBytes).HasColumnType("bytea");
        builder.Property(v => v.DocxFileName).HasMaxLength(256);
        builder.Property(v => v.IsApproved).IsRequired();
        builder.Property(v => v.TranslatorNote).HasColumnType("text");

        // Natural key. The renderer's per-locale lookup expects at most one row per
        // (Template, Language) pair; the unique constraint enforces it at the DB layer.
        builder.HasIndex(v => new { v.TemplateId, v.Language }).IsUnique();
    }
}
