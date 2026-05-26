using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0302 / TOR §2.1 — maps <see cref="ContributorSourceChangeHistory"/> to
/// <c>cnas.ContributorSourceChangeHistory</c>. Append-only history table; the
/// reader UX renders the per-contributor timeline in descending order, hence the
/// composite index on (<c>ContributorId</c>, <c>ChangedAtUtc DESC</c>).
/// </summary>
public sealed class ContributorSourceChangeHistoryConfiguration : AuditableEntityConfiguration<ContributorSourceChangeHistory>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ContributorSourceChangeHistory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ContributorSourceChangeHistory");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.OldSourceSystem).HasMaxLength(64);
        builder.Property(e => e.NewSourceSystem).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ChangedAtUtc).IsRequired();
        builder.Property(e => e.ChangedByUserId);
        builder.Property(e => e.Reason).HasMaxLength(500);

        builder.HasOne(e => e.Contributor)
            .WithMany()
            .HasForeignKey(e => e.ContributorId)
            .OnDelete(DeleteBehavior.Restrict);

        // Per-contributor timeline lookup, descending by event timestamp.
        builder.HasIndex(e => new { e.ContributorId, e.ChangedAtUtc })
            .IsDescending(false, true);
    }
}
