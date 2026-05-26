using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0191 / TOR SEC 050 / TOR ARH 028 — maps <see cref="EntityHistoryRow"/> to
/// <c>cnas.EntityHistoryRows</c>. The composite <c>(EntityType, EntityId,
/// ChangedAtUtc DESC)</c> index drives the per-entity timeline query that the
/// admin REST surface backs; the secondary <c>EntityType</c>-only index serves
/// schema-wide audit sweeps.
/// </summary>
public sealed class EntityHistoryRowConfiguration : AuditableEntityConfiguration<EntityHistoryRow>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<EntityHistoryRow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("EntityHistoryRows");

        builder.Property(e => e.EntityType).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Operation).IsRequired().HasMaxLength(1);
        builder.Property(e => e.PayloadJson).IsRequired();
        builder.Property(e => e.ChangedAtUtc).IsRequired();
        builder.Property(e => e.ActorSqid).HasMaxLength(64);

        // Timeline query path — most-recent first per (entity-type, entity-id).
        // Postgres honours the DESC modifier; the InMemory provider treats it as
        // an unsorted scan but the same predicate still narrows correctly.
        builder.HasIndex(e => new { e.EntityType, e.EntityId, e.ChangedAtUtc })
            .HasDatabaseName("IX_EntityHistoryRows_Type_Id_ChangedAt");

        // Schema-wide "how many UserProfile rows changed last week" sweep.
        builder.HasIndex(e => e.EntityType)
            .HasDatabaseName("IX_EntityHistoryRows_EntityType");
    }
}
