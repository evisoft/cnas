using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DocumentTemplate"/> to <c>cnas.DocumentTemplates</c> — the append-only
/// repository of versioned operator-uploaded DOCX templates introduced in UC17 phase 2A.
/// Mirrors the <see cref="WorkflowDefinitionConfiguration"/> shape (natural-key unique
/// index + partial current-row index) because the two entities share the same
/// "monotonically increasing per code" versioning model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Indexes declared here</b> (the soft-delete + audit-timestamp indexes are
/// contributed by <see cref="AuditableEntityConfiguration{TEntity}"/>):
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE (Code, Version)</c> — natural key. Inserting two rows with the same
///       code and version is a programming error in the service layer; the database
///       constraint converts it into a deterministic <c>DbUpdateException</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(Code, IsCurrent) WHERE IsCurrent = true</c> — partial index supporting the
///       canonical lookup performed by <c>GetAsync</c> / <c>DownloadAsync</c>. Restricting
///       the index to the single "current" row per code keeps it slim regardless of how
///       many historical revisions accumulate.
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Column widths.</b>
/// <list type="bullet">
///   <item><description><c>Code</c> — <c>varchar(96)</c> (kebab-case strings well under this cap).</description></item>
///   <item><description><c>Name</c> — <c>varchar(256)</c> (display label; ample for any reasonable title).</description></item>
///   <item><description><c>Description</c> — <c>text</c> (nullable; free-text usage note).</description></item>
///   <item><description><c>StorageObjectKey</c> — <c>varchar(512)</c> (composed object path).</description></item>
///   <item><description><c>ContentType</c> — <c>varchar(128)</c> (MIME types fit in 128 chars).</description></item>
///   <item><description><c>ContentSha256</c> — <c>char(64)</c> (lower-case hex of SHA-256).</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class DocumentTemplateConfiguration : AuditableEntityConfiguration<DocumentTemplate>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<DocumentTemplate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DocumentTemplates");

        // 96 chars is roomy — the longest production code today is "invitatie-doc-suplimentare"
        // at 26 chars. We pick 96 (vs WorkflowDefinition's 64) because uploaded template
        // codes may carry richer prefixes once operators start versioning by branch/region
        // (e.g. "decizia-pensie-ro-2027"); going wide once is cheaper than a migration.
        builder.Property(t => t.Code).IsRequired().HasMaxLength(96);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(256);

        // text — no upper bound at the database layer for the free-form description.
        // The service layer applies no cap either (it's an admin field; abuse is
        // self-limiting because admins are authenticated and audit-logged).
        builder.Property(t => t.Description).HasColumnType("text");

        builder.Property(t => t.Version).IsRequired();
        builder.Property(t => t.IsCurrent).IsRequired();

        builder.Property(t => t.StorageObjectKey).IsRequired().HasMaxLength(512);
        builder.Property(t => t.ContentType).IsRequired().HasMaxLength(128);
        builder.Property(t => t.ContentLength).IsRequired();

        // char(64) — SHA-256 hex is exactly 64 chars; using char (vs varchar) is a
        // micro-optimisation that pays off when the column appears in a search/where
        // clause because Postgres can skip the length-prefix dance on every row.
        builder.Property(t => t.ContentSha256).IsRequired().HasColumnType("char(64)");

        // R0133 — Fallback locale for the renderer's variant-resolution dispatch. Required
        // non-null with the literal "ro" default (matches the entity-level default and the
        // migration back-fill); width 8 to mirror TemplateVariantConfiguration.Language.
        builder.Property(t => t.DefaultLanguage)
            .IsRequired()
            .HasMaxLength(8)
            .HasDefaultValue("ro");

        // R0131 / CF 17.15 — Optional metadata-driven validation rule-set. Stored as JSONB
        // (Postgres) so future server-side analytics (e.g. "which fields are most often
        // gated by Required?") can introspect without a string parse. Nullable so legacy
        // rows behave unchanged.
        builder.Property(t => t.ValidationRulesJson).HasColumnType("jsonb");

        // Natural key. Inserting a duplicate (Code, Version) is a programming error in
        // the service — the unique constraint converts it into a deterministic
        // DbUpdateException at SaveChanges time.
        builder.HasIndex(t => new { t.Code, t.Version }).IsUnique();

        // Partial index supporting GetAsync / DownloadAsync — the predicate restricts
        // the index to the single "current" row per code, which is exactly the query
        // shape. The HasFilter clause uses Postgres-quoted column name because EF maps
        // the property name to a PascalCase column. Mirrors WorkflowDefinitionConfiguration.
        builder.HasIndex(t => new { t.Code, t.IsCurrent })
            .HasFilter("\"IsCurrent\" = true");
    }
}
