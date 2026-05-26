using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2500 / TOR PIR 020-023 — maps <see cref="SupportTicketComment"/> to
/// <c>cnas.SupportTicketComments</c>. Indexes the (TicketId, PostedAt)
/// pair so the ticket-detail render and the chronological-list path stay
/// cheap.
/// </summary>
public sealed class SupportTicketCommentConfiguration : AuditableEntityConfiguration<SupportTicketComment>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SupportTicketComment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SupportTicketComments");

        builder.Property(e => e.TicketId).IsRequired();
        builder.Property(e => e.AuthorUserId).IsRequired();
        builder.Property(e => e.Body).IsRequired().HasMaxLength(8000);
        builder.Property(e => e.IsInternalOnly).IsRequired().HasDefaultValue(false);
        builder.Property(e => e.PostedAt).IsRequired();

        builder.HasIndex(e => new { e.TicketId, e.PostedAt })
            .HasDatabaseName("IX_SupportTicketComments_TicketId_PostedAt");
    }
}
