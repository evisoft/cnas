using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0933 / TOR §10.1 — maps <see cref="DecisionSupersession"/> to
/// <c>cnas.DecisionSupersessions</c>. Enforces the natural-key uniqueness on
/// the (<see cref="DecisionSupersession.PreviousDecisionId"/>,
/// <see cref="DecisionSupersession.NewDecisionId"/>) pair so the same prior
/// decision cannot be terminated twice through this surface; adds covering
/// indexes for the per-new-decision and per-prior-decision lookup paths.
/// </summary>
public sealed class DecisionSupersessionConfiguration
    : AuditableEntityConfiguration<DecisionSupersession>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<DecisionSupersession> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DecisionSupersessions");

        builder.Property(e => e.PreviousDecisionId).IsRequired();
        builder.Property(e => e.NewDecisionId).IsRequired();
        builder.Property(e => e.SupersededAtUtc).IsRequired();
        builder.Property(e => e.SupersededByUserId);
        builder.Property(e => e.Reason).HasMaxLength(500);

        // Decimal precision matches the MDL convention used across the BenefitPayment / MPay surfaces.
        builder.Property(e => e.PriorAmount).HasPrecision(18, 2);
        builder.Property(e => e.NewAmount).HasPrecision(18, 2);

        // Natural-key uniqueness — one supersession row per (prior, new) decision pair.
        builder.HasIndex(e => new { e.PreviousDecisionId, e.NewDecisionId })
            .IsUnique()
            .HasDatabaseName("UX_DecisionSupersessions_Prior_New");

        // Per-new-decision lookup (used by the GET /api/decisions/{sqid}/compare-with-prior path).
        builder.HasIndex(e => e.NewDecisionId)
            .HasDatabaseName("IX_DecisionSupersessions_NewDecisionId");

        // Per-prior-decision lookup (used by the audit / reporting surfaces).
        builder.HasIndex(e => e.PreviousDecisionId)
            .HasDatabaseName("IX_DecisionSupersessions_PreviousDecisionId");
    }
}
