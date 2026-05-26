using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2003 / R0133 — Maps <see cref="TemplateLanguageCoverageFinding"/> to
/// <c>cnas.TemplateLanguageCoverageFindings</c>. Owns the open-finding
/// dedup unique index (TemplateId, MissingLanguage) filtered to
/// <c>Acknowledged=false</c>, plus two operator-facing indexes used by the
/// admin list endpoint (acknowledgement-state filter + recency sort).
/// </summary>
public sealed class TemplateLanguageCoverageFindingConfiguration
    : AuditableEntityConfiguration<TemplateLanguageCoverageFinding>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<TemplateLanguageCoverageFinding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TemplateLanguageCoverageFindings");

        builder.Property(e => e.TemplateId).IsRequired();
        builder.Property(e => e.MissingLanguage).IsRequired().HasMaxLength(8);
        builder.Property(e => e.DetectedAt).IsRequired();
        builder.Property(e => e.Acknowledged).IsRequired();
        builder.Property(e => e.AcknowledgedAt);
        builder.Property(e => e.AcknowledgedByUserId);
        builder.Property(e => e.AcknowledgementNote).HasMaxLength(1000);

        builder.HasOne<DocumentTemplate>()
            .WithMany()
            .HasForeignKey(e => e.TemplateId)
            .OnDelete(DeleteBehavior.Restrict);

        // Filtered unique index — prevents duplicate OPEN findings for the
        // same (TemplateId, MissingLanguage) pair. Closed (Acknowledged=true)
        // rows fall outside the filter so the audit history is preserved.
        builder.HasIndex(e => new { e.TemplateId, e.MissingLanguage, e.Acknowledged })
            .HasDatabaseName("UX_TemplateLanguageCoverageFindings_Open")
            .IsUnique()
            .HasFilter("\"Acknowledged\" = false");

        builder.HasIndex(e => new { e.MissingLanguage, e.Acknowledged })
            .HasDatabaseName("IX_TemplateLanguageCoverageFindings_Language_Acknowledged");

        builder.HasIndex(e => e.DetectedAt)
            .HasDatabaseName("IX_TemplateLanguageCoverageFindings_DetectedAt")
            .IsDescending();
    }
}
