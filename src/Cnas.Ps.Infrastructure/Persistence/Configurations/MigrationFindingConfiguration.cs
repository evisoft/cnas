using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2430 / R2433 / TOR M4 — maps <see cref="MigrationFinding"/> to
/// <c>cnas.MigrationFindings</c>. Three indexes back the admin workload:
/// per-run drilldown, the acknowledgement worklist, and per-finding-code
/// frequency reports.
/// </summary>
public sealed class MigrationFindingConfiguration : AuditableEntityConfiguration<MigrationFinding>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MigrationFinding> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MigrationFindings");

        builder.Property(e => e.RunId).IsRequired();
        builder.Property(e => e.BatchOrdinal).IsRequired();
        builder.Property(e => e.RowOrdinalInBatch).IsRequired();
        builder.Property(e => e.Severity)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.FindingCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Description).IsRequired().HasMaxLength(500);
        builder.Property(e => e.SourceFingerprint).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Acknowledged).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.AcknowledgedAt);
        builder.Property(e => e.AcknowledgedByUserId);
        builder.Property(e => e.AcknowledgementNote).HasMaxLength(1000);

        builder.HasIndex(e => new { e.RunId, e.BatchOrdinal })
            .HasDatabaseName("IX_MigrationFindings_RunId_BatchOrdinal");

        builder.HasIndex(e => new { e.Severity, e.Acknowledged })
            .HasDatabaseName("IX_MigrationFindings_Severity_Acknowledged");

        builder.HasIndex(e => e.FindingCode)
            .HasDatabaseName("IX_MigrationFindings_FindingCode");
    }
}
