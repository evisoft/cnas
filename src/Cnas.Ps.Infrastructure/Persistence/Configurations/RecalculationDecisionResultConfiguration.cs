using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1503 / TOR §3.7-D — maps <see cref="RecalculationDecisionResult"/> to
/// <c>cnas.RecalculationDecisionResults</c>. Indexes back the per-run
/// drill-down (<c>RunId</c>), the filtered per-run query (<c>(Status, RunId)</c>),
/// and the per-beneficiary forensic lookup (<c>BeneficiaryIdnpHash</c>).
/// </summary>
public sealed class RecalculationDecisionResultConfiguration
    : AuditableEntityConfiguration<RecalculationDecisionResult>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<RecalculationDecisionResult> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RecalculationDecisionResults");

        builder.Property(r => r.RunId).IsRequired();
        builder.Property(r => r.BenefitDecisionId).IsRequired();
        builder.Property(r => r.BenefitType).IsRequired().HasMaxLength(64);
        builder.Property(r => r.BeneficiaryIdnpHash).IsRequired().HasMaxLength(64);
        builder.Property(r => r.OldAmountMdl).HasPrecision(18, 2).IsRequired();
        builder.Property(r => r.NewAmountMdl).HasPrecision(18, 2).IsRequired();
        builder.Property(r => r.DeltaMdl).HasPrecision(18, 2).IsRequired();
        builder.Property(r => r.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(r => r.Reason).HasMaxLength(500);
        builder.Property(r => r.RecalculationContextJson).HasMaxLength(16384);
        builder.Property(r => r.AppliedAt);

        builder.HasIndex(r => r.RunId)
            .HasDatabaseName("IX_RecalculationDecisionResults_RunId");

        builder.HasIndex(r => new { r.Status, r.RunId })
            .HasDatabaseName("IX_RecalculationDecisionResults_Status_RunId");

        builder.HasIndex(r => r.BeneficiaryIdnpHash)
            .HasDatabaseName("IX_RecalculationDecisionResults_BeneficiaryIdnpHash");
    }
}
