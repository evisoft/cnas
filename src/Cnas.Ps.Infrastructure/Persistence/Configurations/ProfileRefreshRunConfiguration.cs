using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0363 — maps <see cref="ProfileRefreshRun"/> to <c>cnas.ProfileRefreshRuns</c>.
/// Indexed by <c>(Source, StartedUtc DESC)</c> for the operator query "most recent runs
/// against RSP".
/// </summary>
public sealed class ProfileRefreshRunConfiguration : AuditableEntityConfiguration<ProfileRefreshRun>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ProfileRefreshRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProfileRefreshRuns");

        builder.Property(e => e.Source).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Outcome).IsRequired();
        builder.Property(e => e.RowsApplied).IsRequired();
        builder.Property(e => e.RowsSkipped).IsRequired();
        builder.Property(e => e.StartedUtc).IsRequired();
        builder.Property(e => e.FailureSummary).HasMaxLength(5000);

        builder.HasIndex(e => new { e.Source, e.StartedUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_ProfileRefreshRuns_Source_StartedUtcDesc");
        builder.HasIndex(e => e.TargetContributorId)
            .HasDatabaseName("IX_ProfileRefreshRuns_TargetContributorId");
    }
}
