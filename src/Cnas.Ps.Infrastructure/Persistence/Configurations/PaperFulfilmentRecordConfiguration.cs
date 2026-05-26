using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0602 / TOR CF 11.03 — maps <see cref="PaperFulfilmentRecord"/> to
/// <c>cnas.PaperFulfilmentRecords</c>. Enforces a unique <c>DocumentId</c>
/// (one fulfilment per document) plus a covering index on
/// <c>(Status, TerritorialSubdivisionCode)</c> for the operator queue view.
/// </summary>
public sealed class PaperFulfilmentRecordConfiguration
    : AuditableEntityConfiguration<PaperFulfilmentRecord>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PaperFulfilmentRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PaperFulfilmentRecords");

        builder.Property(e => e.DocumentId).IsRequired();
        builder.Property(e => e.TerritorialSubdivisionCode).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.EnqueuedAtUtc).IsRequired();
        builder.Property(e => e.PrintedAtUtc);
        builder.Property(e => e.DispatchedAtUtc);
        builder.Property(e => e.DeliveredOn);
        builder.Property(e => e.CarrierTrackingNumber).HasMaxLength(64);

        builder.HasIndex(e => e.DocumentId)
            .IsUnique()
            .HasDatabaseName("UX_PaperFulfilmentRecords_DocumentId");

        builder.HasIndex(e => new { e.Status, e.TerritorialSubdivisionCode })
            .HasDatabaseName("IX_PaperFulfilmentRecords_Status_Subdivision");
    }
}
