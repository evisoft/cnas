using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0166 / TOR CF 03.11 / UI 015 — maps <see cref="BulkOperationRun"/> to
/// <c>cnas.BulkOperationRuns</c>. One row per <c>POST /api/bulk-actions/runs</c>;
/// captures the lifecycle status, the row counters, the timestamps, the per-row
/// failure summary, and the optional idempotency key.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE (ActorUserId, OperationCode, IdempotencyKey) WHERE IdempotencyKey IS NOT NULL</c>
///       — the idempotency natural key. Implemented as a partial unique index so a
///       null idempotency key (the no-de-duplication path) does not collide with
///       other null-key rows.
///     </description>
///   </item>
///   <item>
///     <description><c>(BulkSelectionId)</c> — supports join-back from a selection row to its
///     consuming run (used by audit-trail forensics).</description>
///   </item>
///   <item>
///     <description><c>(ActorUserId)</c> — supports per-actor listings.</description>
///   </item>
/// </list>
/// </remarks>
public sealed class BulkOperationRunConfiguration : AuditableEntityConfiguration<BulkOperationRun>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<BulkOperationRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BulkOperationRuns");

        builder.Property(r => r.BulkSelectionId).IsRequired();
        builder.Property(r => r.OperationCode).IsRequired().HasMaxLength(64);
        builder.Property(r => r.ActorUserId).IsRequired();

        // Persist the enum as its string name so a future enum reorder (or a new
        // intermediate state) does not silently corrupt persisted rows. The audit
        // trail and the API contract both surface the status as a stable string.
        builder.Property(r => r.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(r => r.TotalRows).IsRequired();
        builder.Property(r => r.SucceededRows).IsRequired();
        builder.Property(r => r.FailedRows).IsRequired();
        builder.Property(r => r.StartedUtc).IsRequired();
        builder.Property(r => r.CompletedUtc);
        builder.Property(r => r.ParametersJson).HasColumnType("text");
        builder.Property(r => r.IdempotencyKey).HasMaxLength(128);
        builder.Property(r => r.FailureSummaryJson).HasColumnType("text");

        builder.HasIndex(r => r.BulkSelectionId);
        builder.HasIndex(r => r.ActorUserId);

        // Partial unique index over the idempotency triple. The filter expression
        // ensures null keys do not collide with each other; non-null keys form a
        // strict natural key.
        builder.HasIndex(r => new { r.ActorUserId, r.OperationCode, r.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");
    }
}
