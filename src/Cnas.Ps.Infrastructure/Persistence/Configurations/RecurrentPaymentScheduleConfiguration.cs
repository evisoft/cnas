using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — EF Core configuration for
/// <see cref="RecurrentPaymentSchedule"/>. Maps the entity to
/// <c>cnas.RecurrentPaymentSchedules</c>. An index on (IsActive,
/// NextPaymentDate) accelerates the <c>RecurrentPaymentJob</c> "due now"
/// sweep.
/// </summary>
public sealed class RecurrentPaymentScheduleConfiguration : AuditableEntityConfiguration<RecurrentPaymentSchedule>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<RecurrentPaymentSchedule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RecurrentPaymentSchedules");

        builder.Property(e => e.BeneficiaryId).IsRequired();
        builder.Property(e => e.ServiceCode).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Amount).IsRequired().HasColumnType("decimal(18, 2)");
        builder.Property(e => e.NextPaymentDate).IsRequired();
        builder.Property(e => e.Cadence)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);
        builder.Property(e => e.LastPaymentAtUtc);
        builder.Property(e => e.FailureCount).IsRequired().HasDefaultValue(0);
        // Optional FK-shaped link to the last-emitted MPayOrder row. The
        // callback advancer compares the confirmed order's Id against this
        // value to decide whether to advance NextPaymentDate.
        builder.Property(e => e.LastDispatchedOrderId);

        builder.HasIndex(e => new { e.IsActive, e.NextPaymentDate })
            .HasDatabaseName("IX_RecurrentPaymentSchedules_IsActive_NextPaymentDate");

        builder.HasIndex(e => new { e.BeneficiaryId, e.ServiceCode })
            .HasDatabaseName("IX_RecurrentPaymentSchedules_BeneficiaryId_ServiceCode");
    }
}
