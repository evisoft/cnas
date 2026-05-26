using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R2500 / TOR PIR 020-023 — maps <see cref="SupportTicketCategory"/> to
/// <c>cnas.SupportTicketCategories</c>. Enforces a unique <c>Code</c> and
/// an index on <c>IsActive</c> for the admin "list active categories" path.
/// </summary>
public sealed class SupportTicketCategoryConfiguration : AuditableEntityConfiguration<SupportTicketCategory>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<SupportTicketCategory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SupportTicketCategories");

        builder.Property(e => e.Code).IsRequired().HasMaxLength(64);
        builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(256);
        // R0027 / TOR ARH 022 — optional per-locale name columns. Nullable so the
        // resolver falls back to DisplayName when not curated.
        builder.Property(e => e.NameRo).HasMaxLength(256);
        builder.Property(e => e.NameRu).HasMaxLength(256);
        builder.Property(e => e.NameEn).HasMaxLength(256);
        builder.Property(e => e.Description).HasMaxLength(1000);
        builder.Property(e => e.DefaultSeverity)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.FirstResponseSlaMinutes).IsRequired();
        builder.Property(e => e.ResolutionSlaMinutes).IsRequired();
        builder.Property(e => e.EscalationQueueCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.RegisteredByUserId).IsRequired();

        builder.HasIndex(e => e.Code)
            .IsUnique()
            .HasDatabaseName("UX_SupportTicketCategories_Code");

        builder.HasIndex(e => e.IsActive)
            .HasDatabaseName("IX_SupportTicketCategories_IsActive_Helpdesk");
    }
}
