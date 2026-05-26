using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0115 / TOR CF 14.07 — maps <see cref="MNotifyTemplate"/> to
/// <c>cnas.MNotifyTemplates</c>. Enforces a unique <c>Code</c> natural key and
/// stores <c>ChannelKind</c> as the stable enum-name string (so the wire
/// contract survives renumbering).
/// </summary>
public sealed class MNotifyTemplateConfiguration : AuditableEntityConfiguration<MNotifyTemplate>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MNotifyTemplate> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MNotifyTemplates");

        builder.Property(e => e.Code).IsRequired().HasMaxLength(MNotifyTemplate.MaxCodeLength);
        builder.Property(e => e.ChannelKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(16);
        builder.Property(e => e.Subject).HasMaxLength(MNotifyTemplate.MaxSubjectLength);
        builder.Property(e => e.BodyMarkdown).IsRequired().HasMaxLength(MNotifyTemplate.MaxBodyLength);
        builder.Property(e => e.UpdatedByUserId);

        builder.HasIndex(e => e.Code)
            .IsUnique()
            .HasDatabaseName("UX_MNotifyTemplates_Code");

        builder.HasIndex(e => new { e.ChannelKind, e.IsActive })
            .HasDatabaseName("IX_MNotifyTemplates_ChannelKind_IsActive");
    }
}
