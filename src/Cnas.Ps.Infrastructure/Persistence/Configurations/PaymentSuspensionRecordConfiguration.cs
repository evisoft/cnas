using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1504 / TOR §3.7-E — maps <see cref="PaymentSuspensionRecord"/> to
/// <c>cnas.PaymentSuspensionRecords</c>. Indexes the active subset of
/// suspensions per decision and the chronological browse path operators use
/// from the admin UI.
/// </summary>
public sealed class PaymentSuspensionRecordConfiguration
    : AuditableEntityConfiguration<PaymentSuspensionRecord>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PaymentSuspensionRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PaymentSuspensionRecords");

        builder.Property(e => e.DecisionId).IsRequired();
        builder.Property(e => e.SuspendedAtUtc).IsRequired();
        builder.Property(e => e.SuspendedByUserId).IsRequired();
        builder.Property(e => e.SuspensionReason).IsRequired().HasMaxLength(500);
        builder.Property(e => e.ResumedAtUtc);
        builder.Property(e => e.ResumedByUserId);
        builder.Property(e => e.ResumeReason).HasMaxLength(500);
        builder.Property(e => e.SuspensionDocumentId);
        builder.Property(e => e.ResumeDocumentId);

        // Composite index supporting "find the active suspension for this decision"
        // — the double-suspend guard in PaymentSuspensionService.SuspendAsync hits
        // (DecisionId, ResumedAtUtc IS NULL).
        builder.HasIndex(e => new { e.DecisionId, e.ResumedAtUtc })
            .HasDatabaseName("IX_PaymentSuspensionRecords_DecisionId_ResumedAtUtc");

        // Browse-by-time index for the admin list page.
        builder.HasIndex(e => e.SuspendedAtUtc)
            .IsDescending()
            .HasDatabaseName("IX_PaymentSuspensionRecords_SuspendedAtUtc_Desc");
    }
}
