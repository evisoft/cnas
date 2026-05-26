using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1403 / TOR §3.6-D — maps <see cref="AthleteCareerRecord"/> to
/// <c>cnas.AthleteCareerRecords</c>. Indexed by
/// <see cref="AthleteCareerRecord.AwardId"/> for per-award lookup and by
/// <c>(AchievementKind, AchievementYear)</c> for cross-award reporting.
/// </summary>
public sealed class AthleteCareerRecordConfiguration
    : AuditableEntityConfiguration<AthleteCareerRecord>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<AthleteCareerRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AthleteCareerRecords");

        builder.Property(e => e.AwardId).IsRequired();
        builder.Property(e => e.AchievementKind)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        builder.Property(e => e.AchievementYear).IsRequired();
        builder.Property(e => e.Event).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Years);
        builder.Property(e => e.Verified).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.VerifiedAt);
        builder.Property(e => e.VerifiedByUserId);
        builder.Property(e => e.VerificationNote).HasMaxLength(1000);
        builder.Property(e => e.EvidenceDocumentReference).HasMaxLength(256);

        builder.HasIndex(e => e.AwardId)
            .HasDatabaseName("IX_AthleteCareerRecords_AwardId");

        builder.HasIndex(e => new { e.AchievementKind, e.AchievementYear })
            .HasDatabaseName("IX_AthleteCareerRecords_Kind_Year");
    }
}
