using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — maps
/// <see cref="IntlAgreementReviewStep"/> to
/// <c>cnas.IntlAgreementReviewSteps</c>. Indexed by
/// <see cref="IntlAgreementReviewStep.CaseId"/> for per-case lookup and by
/// <c>(CaseId, ReviewedAt)</c> for ordered replay.
/// </summary>
public sealed class IntlAgreementReviewStepConfiguration
    : AuditableEntityConfiguration<IntlAgreementReviewStep>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<IntlAgreementReviewStep> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IntlAgreementReviewSteps");

        builder.Property(e => e.CaseId).IsRequired();
        builder.Property(e => e.Level)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        builder.Property(e => e.Outcome)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        builder.Property(e => e.ReviewedAt).IsRequired();
        builder.Property(e => e.ReviewedByUserId).IsRequired();
        builder.Property(e => e.Note).IsRequired().HasMaxLength(2000);

        builder.HasIndex(e => e.CaseId)
            .HasDatabaseName("IX_IntlAgreementReviewSteps_CaseId");

        builder.HasIndex(e => new { e.CaseId, e.ReviewedAt })
            .HasDatabaseName("IX_IntlAgreementReviewSteps_CaseId_ReviewedAt");
    }
}
