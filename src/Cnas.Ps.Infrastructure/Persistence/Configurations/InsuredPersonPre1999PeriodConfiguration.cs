using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0921 / TOR BP 2.3-B — maps <see cref="InsuredPersonPre1999Period"/> to
/// <c>cnas.InsuredPersonPre1999Periods</c>. Composite index on
/// <c>(InsuredPersonSolicitantId, PeriodStartDate)</c> backs the per-citizen
/// ascending-date listing endpoint; a second index on <c>LaborBookletId</c>
/// backs the per-booklet drill-down.
/// </summary>
public sealed class InsuredPersonPre1999PeriodConfiguration
    : AuditableEntityConfiguration<InsuredPersonPre1999Period>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<InsuredPersonPre1999Period> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("InsuredPersonPre1999Periods");

        builder.Property(e => e.InsuredPersonSolicitantId).IsRequired();
        builder.Property(e => e.LaborBookletId);
        builder.Property(e => e.PeriodStartDate).IsRequired();
        builder.Property(e => e.PeriodEndDate).IsRequired();
        builder.Property(e => e.EmployerName).HasMaxLength(200);
        builder.Property(e => e.Position).HasMaxLength(200);
        builder.Property(e => e.DaysWorked);
        builder.Property(e => e.ProofDocumentReference).HasMaxLength(200);
        builder.Property(e => e.Notes).HasMaxLength(500);
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ValidToUtc);
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => new { e.InsuredPersonSolicitantId, e.PeriodStartDate })
            .HasDatabaseName("IX_InsuredPersonPre1999Periods_Solicitant_StartDate");

        builder.HasIndex(e => e.LaborBookletId)
            .HasDatabaseName("IX_InsuredPersonPre1999Periods_LaborBookletId");
    }
}
