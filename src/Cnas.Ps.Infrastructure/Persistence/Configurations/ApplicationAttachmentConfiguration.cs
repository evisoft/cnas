using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0322 / TOR UI 014 — maps <see cref="ApplicationAttachment"/> to
/// <c>cnas.ApplicationAttachments</c>. The table is the rich per-link metadata
/// record sitting alongside the legacy denormalised
/// <c>ServiceApplication.AttachmentDocumentIds</c> list.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>
///     <c>UNIQUE (ApplicationId, DocumentId) WHERE RemovedAtUtc IS NULL</c> —
///     enforces "at most one active link per (application, document)" while
///     allowing the citizen to re-attach the same document after removing it
///     (the filtered predicate excludes removed rows).
///   </description></item>
///   <item><description>
///     <c>(ApplicationId, AttachedAtUtc DESC)</c> — supports the per-application
///     listing query path ordered most-recent-first.
///   </description></item>
///   <item><description>
///     <c>(VirusScanStatus, AttachedAtUtc)</c> — supports the virus-scan
///     orchestrator's "find all Pending rows" sweep.
///   </description></item>
/// </list>
/// </remarks>
public sealed class ApplicationAttachmentConfiguration : AuditableEntityConfiguration<ApplicationAttachment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ApplicationAttachment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ApplicationAttachments");

        builder.Property(e => e.ApplicationId).IsRequired();
        builder.Property(e => e.DocumentId).IsRequired();
        builder.Property(e => e.Category)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.IsMandatorySnapshot).IsRequired();
        builder.Property(e => e.AttachedByUserId).IsRequired();
        builder.Property(e => e.AttachedAtUtc).IsRequired();
        builder.Property(e => e.VirusScanStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.VirusScannedAtUtc);
        builder.Property(e => e.VirusScannerName).HasMaxLength(64);
        builder.Property(e => e.Notes).HasMaxLength(500);
        builder.Property(e => e.RemovedAtUtc);
        builder.Property(e => e.RemovedByUserId);
        builder.Property(e => e.RemovalReason).HasMaxLength(500);

        builder.HasOne(e => e.Application)
            .WithMany()
            .HasForeignKey(e => e.ApplicationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Document)
            .WithMany()
            .HasForeignKey(e => e.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Per-(application, document) unique while the link is active.
        builder.HasIndex(e => new { e.ApplicationId, e.DocumentId })
            .IsUnique()
            .HasFilter("\"RemovedAtUtc\" IS NULL");

        // Per-application listing query, most-recent-first.
        builder.HasIndex(e => new { e.ApplicationId, e.AttachedAtUtc })
            .IsDescending(false, true);

        // Virus-scan sweep: find Pending rows oldest-first.
        builder.HasIndex(e => new { e.VirusScanStatus, e.AttachedAtUtc });
    }
}
