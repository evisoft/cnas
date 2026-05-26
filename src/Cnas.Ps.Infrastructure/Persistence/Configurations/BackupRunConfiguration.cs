using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2307 / TOR SEC 060 — maps <see cref="BackupRun"/> to
/// <c>cnas.BackupRuns</c>. Enforces unique <c>RunNumber</c> and indexes the
/// admin list (PolicyId, StartedAt DESC) + retention-sweep predicate
/// (Status, RetentionPurgedAt).
/// </summary>
public sealed class BackupRunConfiguration : AuditableEntityConfiguration<BackupRun>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<BackupRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BackupRuns");

        builder.Property(e => e.PolicyId).IsRequired();
        builder.Property(e => e.RunNumber).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.TriggerKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.StartedAt).IsRequired();
        builder.Property(e => e.CompletedAt);
        builder.Property(e => e.DurationMs);
        builder.Property(e => e.PayloadSizeBytes);
        builder.Property(e => e.PayloadHashSha256).HasMaxLength(64);
        builder.Property(e => e.PayloadStorageKey).HasMaxLength(512);
        builder.Property(e => e.FailureReason).HasMaxLength(1000);
        builder.Property(e => e.RetentionPurgedAt);

        builder.HasIndex(e => e.RunNumber)
            .IsUnique()
            .HasDatabaseName("UX_BackupRuns_RunNumber");

        builder.HasIndex(e => new { e.PolicyId, e.StartedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_BackupRuns_PolicyId_StartedAtDesc");

        builder.HasIndex(e => new { e.Status, e.RetentionPurgedAt })
            .HasDatabaseName("IX_BackupRuns_Status_RetentionPurgedAt");
    }
}
