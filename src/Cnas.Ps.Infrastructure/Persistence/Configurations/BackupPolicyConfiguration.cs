using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2307 / TOR SEC 060 — maps <see cref="BackupPolicy"/> to
/// <c>cnas.BackupPolicies</c>. Enforces unique <c>PolicyCode</c> and a
/// composite index on (IsActive, Scope) for the orchestrator's "policies due
/// now" lookup.
/// </summary>
public sealed class BackupPolicyConfiguration : AuditableEntityConfiguration<BackupPolicy>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<BackupPolicy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BackupPolicies");

        builder.Property(e => e.PolicyCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasMaxLength(2000);
        builder.Property(e => e.Scope)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Strategy)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.CronSchedule).IsRequired().HasMaxLength(64);
        builder.Property(e => e.RetentionDays).IsRequired().HasDefaultValue(30);
        builder.Property(e => e.TargetKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.TargetReference).HasMaxLength(256);
        builder.Property(e => e.LastSuccessfulRunAt);
        builder.Property(e => e.LastFailedRunAt);
        builder.Property(e => e.RegisteredByUserId).IsRequired();
        builder.Property(e => e.IsArchived).IsRequired().HasDefaultValue(false);

        builder.HasIndex(e => e.PolicyCode)
            .IsUnique()
            .HasDatabaseName("UX_BackupPolicies_PolicyCode");

        builder.HasIndex(e => new { e.IsActive, e.Scope })
            .HasDatabaseName("IX_BackupPolicies_IsActive_Scope");
    }
}
