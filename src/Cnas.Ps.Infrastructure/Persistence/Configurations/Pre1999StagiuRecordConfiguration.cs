using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — maps <see cref="Pre1999StagiuRecord"/> to the
/// <c>cnas.Pre1999StagiuRecords</c> table.
/// </summary>
public sealed class Pre1999StagiuRecordConfiguration : AuditableEntityConfiguration<Pre1999StagiuRecord>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<Pre1999StagiuRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Pre1999StagiuRecords");

        builder.Property(e => e.InsuredPersonId).IsRequired();
        builder.Property(e => e.FromDate).IsRequired();
        builder.Property(e => e.ToDate).IsRequired();
        builder.Property(e => e.Years).IsRequired();
        builder.Property(e => e.Months).IsRequired();
        builder.Property(e => e.Days).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(200);
        builder.Property(e => e.Notes).HasMaxLength(500);

        // Composite index supports the per-citizen ascending-date listing
        // endpoint (`ListAsync` orders by FromDate ascending). Mirrors the
        // sibling InsuredPersonPre1999Period configuration's pattern.
        builder.HasIndex(e => new { e.InsuredPersonId, e.FromDate });
    }
}
