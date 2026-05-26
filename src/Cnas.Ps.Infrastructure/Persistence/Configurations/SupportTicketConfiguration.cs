using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2500 / TOR PIR 020-023 — maps <see cref="SupportTicket"/> to
/// <c>cnas.SupportTickets</c>. Enforces a unique <c>TicketNumber</c> and the
/// composite indexes required by the SLA evaluator + admin grid lookups.
/// </summary>
public sealed class SupportTicketConfiguration : AuditableEntityConfiguration<SupportTicket>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SupportTicket> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SupportTickets");

        builder.Property(e => e.TicketNumber).IsRequired().HasMaxLength(32);
        builder.Property(e => e.CategoryId).IsRequired();
        builder.Property(e => e.Title).IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).IsRequired().HasMaxLength(8000);
        builder.Property(e => e.Severity)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.SubmittedByUserId).IsRequired();
        builder.Property(e => e.AssignedToUserId);
        builder.Property(e => e.SubmittedAt).IsRequired();
        builder.Property(e => e.FirstAcknowledgedAt);
        builder.Property(e => e.ResolvedAt);
        builder.Property(e => e.ClosedAt);
        builder.Property(e => e.FirstResponseDueAt).IsRequired();
        builder.Property(e => e.ResolutionDueAt).IsRequired();
        builder.Property(e => e.EscalatedAt);
        builder.Property(e => e.EscalationReason).HasMaxLength(500);
        builder.Property(e => e.ResolutionSummary).HasMaxLength(2000);
        builder.Property(e => e.CancelReason).HasMaxLength(500);

        builder.HasIndex(e => e.TicketNumber)
            .IsUnique()
            .HasDatabaseName("UX_SupportTickets_TicketNumber");

        builder.HasIndex(e => new { e.Status, e.FirstResponseDueAt })
            .HasDatabaseName("IX_SupportTickets_Status_FirstResponseDueAt");

        builder.HasIndex(e => new { e.Status, e.ResolutionDueAt })
            .HasDatabaseName("IX_SupportTickets_Status_ResolutionDueAt");

        builder.HasIndex(e => new { e.SubmittedByUserId, e.SubmittedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_SupportTickets_SubmittedBy_SubmittedAtDesc");

        builder.HasIndex(e => new { e.CategoryId, e.Status })
            .HasDatabaseName("IX_SupportTickets_CategoryId_Status");
    }
}
