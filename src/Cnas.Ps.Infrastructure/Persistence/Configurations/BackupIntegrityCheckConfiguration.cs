using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2307 / TOR SEC 060 — maps <see cref="BackupIntegrityCheck"/> to
/// <c>cnas.BackupIntegrityChecks</c>. Enforces a unique constraint on
/// <c>RunId</c> so a re-verification updates the existing row in place
/// rather than appending duplicates.
/// </summary>
public sealed class BackupIntegrityCheckConfiguration : AuditableEntityConfiguration<BackupIntegrityCheck>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<BackupIntegrityCheck> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BackupIntegrityChecks");

        builder.Property(e => e.RunId).IsRequired();
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.CheckedAt).IsRequired();
        builder.Property(e => e.ExpectedHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ActualHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.FailureReason).HasMaxLength(1000);

        builder.HasIndex(e => e.RunId)
            .IsUnique()
            .HasDatabaseName("UX_BackupIntegrityChecks_RunId");
    }
}
