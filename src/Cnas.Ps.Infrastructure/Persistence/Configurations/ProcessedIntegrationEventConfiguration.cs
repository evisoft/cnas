using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0103 / TOR CF 14.02 — maps <see cref="ProcessedIntegrationEvent"/> to
/// <c>cnas.ProcessedIntegrationEvents</c>. The table is the inbound
/// integration-event dedup ledger consulted by
/// <c>IIntegrationEventDeduper.TryClaimAsync</c> before the dispatcher
/// invokes downstream handlers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicity primitive.</b> The UNIQUE index on
/// <see cref="ProcessedIntegrationEvent.MessageId"/> is the race-free anchor
/// behind <c>IIntegrationEventDeduper.TryClaimAsync</c>: two concurrent
/// dispatchers can both reach the insert step, exactly one wins, and the
/// loser receives a 23505 unique-violation that the deduper translates into
/// the same "already processed" outcome.
/// </para>
/// <para>
/// <b>Index portfolio.</b>
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE(MessageId)</c> — the canonical dedup key.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(ProcessedAtUtc DESC)</c> standalone — supports the ops
///       dashboard's "latest 100 processed events" view and the
///       eventual retention sweep.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(Source, Type, ProcessedAtUtc DESC)</c> — supports the
///       "events received from RSP in the last hour" diagnostic.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Outcome storage.</b> <see cref="ProcessedIntegrationEvent.Outcome"/> is
/// stored as a string (enum name) so adding future outcomes does not require
/// a numeric-discriminator migration. The conversion uses EF Core's built-in
/// enum-to-string helper.
/// </para>
/// </remarks>
public sealed class ProcessedIntegrationEventConfiguration : AuditableEntityConfiguration<ProcessedIntegrationEvent>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ProcessedIntegrationEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProcessedIntegrationEvents");

        builder.Property(e => e.MessageId).IsRequired().HasMaxLength(128);
        builder.Property(e => e.Source).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Type).IsRequired().HasMaxLength(256);
        builder.Property(e => e.ProcessedAtUtc).IsRequired();
        builder.Property(e => e.Outcome)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(e => e.FailureReason).HasMaxLength(1000);

        // Canonical dedup primitive — UNIQUE on MessageId.
        builder.HasIndex(e => e.MessageId).IsUnique();

        // Ops dashboard cross-source view.
        builder.HasIndex(e => e.ProcessedAtUtc);

        // Per-stream diagnostic view (Source × Type × time).
        builder.HasIndex(e => new { e.Source, e.Type, e.ProcessedAtUtc });
    }
}
