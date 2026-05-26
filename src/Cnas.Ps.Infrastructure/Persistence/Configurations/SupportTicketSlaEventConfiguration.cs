using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2500 / TOR PIR 020-023 — maps <see cref="SupportTicketSlaEvent"/> to
/// <c>cnas.SupportTicketSlaEvents</c>. Indexes (TicketId, DetectedAt DESC)
/// so the ticket detail surface can return the latest events first; the
/// idempotency predicate (TicketId, EventKind) is enforced application-side
/// inside <c>SupportTicketSlaEvaluator</c>.
/// </summary>
public sealed class SupportTicketSlaEventConfiguration : AuditableEntityConfiguration<SupportTicketSlaEvent>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SupportTicketSlaEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SupportTicketSlaEvents");

        builder.Property(e => e.TicketId).IsRequired();
        builder.Property(e => e.EventKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.DetectedAt).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(1000);

        builder.HasIndex(e => new { e.TicketId, e.DetectedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_SupportTicketSlaEvents_TicketId_DetectedAtDesc");
    }
}
