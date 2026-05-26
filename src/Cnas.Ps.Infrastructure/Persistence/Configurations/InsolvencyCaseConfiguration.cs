using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0830 / R0834 / TOR Annex 1 §8.1.4.5 — maps <see cref="InsolvencyCase"/>
/// to <c>cnas.InsolvencyCases</c>. Indexes:
/// <list type="bullet">
///   <item><description><c>(ContributorId, Status)</c> — per-payer "is there an open case?" probe.</description></item>
///   <item><description><c>(Status, OpenedAtUtc)</c> — backs <c>ListActiveAsync</c>.</description></item>
/// </list>
/// </summary>
public sealed class InsolvencyCaseConfiguration : AuditableEntityConfiguration<InsolvencyCase>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<InsolvencyCase> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("InsolvencyCases");

        builder.Property(e => e.ContributorId).IsRequired();
        builder.Property(e => e.InsolvencyDate).IsRequired();
        builder.Property(e => e.Reason).IsRequired().HasMaxLength(500);
        builder.Property(e => e.Status).IsRequired().HasConversion<int>();
        builder.Property(e => e.OpenedAtUtc).IsRequired();
        builder.Property(e => e.ResolvedAtUtc);
        builder.Property(e => e.Resolution).HasMaxLength(500);

        builder.HasIndex(e => new { e.ContributorId, e.Status })
            .HasDatabaseName("IX_InsolvencyCases_Contributor_Status");

        builder.HasIndex(e => new { e.Status, e.OpenedAtUtc })
            .HasDatabaseName("IX_InsolvencyCases_Status_OpenedAtUtc");

        // iter-149 — partial unique index that enforces the "at most one Open
        // insolvency case per contributor" invariant at the database layer.
        // InsolvencyCaseStatus.Open is the int literal 0 (see Enums.cs); we
        // hard-code the literal here because EF Core does not accept enum names
        // inside the HasFilter expression and the value is part of the
        // persistence contract anyway (renumbering would be a breaking change
        // documented on the enum). The InMemory provider used by integration
        // tests ignores HasFilter, so the pre-check + DbUpdateException catch
        // in OpenAsync remain the only TOCTOU defence under test; under Postgres
        // this index is the load-bearing layer.
        builder.HasIndex(e => e.ContributorId)
            .HasFilter("\"Status\" = 0")
            .IsUnique()
            .HasDatabaseName("UX_InsolvencyCases_Open_Per_Contributor");
    }
}

/// <summary>
/// R0834 — maps <see cref="InsolvencyClaim"/> to <c>cnas.InsolvencyClaims</c>.
/// </summary>
public sealed class InsolvencyClaimConfiguration : AuditableEntityConfiguration<InsolvencyClaim>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<InsolvencyClaim> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("InsolvencyClaims");

        builder.Property(e => e.InsolvencyCaseId).IsRequired();
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.Currency).IsRequired().HasMaxLength(3);
        builder.Property(e => e.Description).IsRequired().HasMaxLength(1000);
        builder.Property(e => e.IncurredOn).IsRequired();

        builder.HasIndex(e => new { e.InsolvencyCaseId, e.IncurredOn })
            .HasDatabaseName("IX_InsolvencyClaims_Case_IncurredOn");
    }
}

/// <summary>
/// R0834 — maps <see cref="InsolvencyPayment"/> to <c>cnas.InsolvencyPayments</c>.
/// </summary>
public sealed class InsolvencyPaymentConfiguration : AuditableEntityConfiguration<InsolvencyPayment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<InsolvencyPayment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("InsolvencyPayments");

        builder.Property(e => e.InsolvencyCaseId).IsRequired();
        builder.Property(e => e.Amount).HasPrecision(18, 2);
        builder.Property(e => e.PaymentDate).IsRequired();
        builder.Property(e => e.Reference).HasMaxLength(64);

        builder.HasIndex(e => new { e.InsolvencyCaseId, e.PaymentDate })
            .HasDatabaseName("IX_InsolvencyPayments_Case_PaymentDate");
    }
}
