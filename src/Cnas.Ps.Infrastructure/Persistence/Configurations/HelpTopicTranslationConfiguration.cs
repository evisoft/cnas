using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="HelpTopicTranslation"/> to <c>cnas.HelpTopicTranslations</c> — the
/// per-language title + body rows for <see cref="HelpTopic"/> (R0225 / TOR UI 015).
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural key.</b> Composite UNIQUE on
/// (<see cref="HelpTopicTranslation.HelpTopicId"/>, <see cref="HelpTopicTranslation.Language"/>).
/// </para>
/// <para>
/// <b>Foreign key.</b> Cascade-delete from <see cref="HelpTopic"/>.
/// </para>
/// <para>
/// <b>Column caps.</b> 8-char <see cref="HelpTopicTranslation.Language"/>, 200-char
/// <see cref="HelpTopicTranslation.Title"/>, 20_000-char
/// <see cref="HelpTopicTranslation.BodyMarkdown"/>, 1024-char
/// <see cref="HelpTopicTranslation.TranslatorNote"/>.
/// </para>
/// </remarks>
public sealed class HelpTopicTranslationConfiguration
    : AuditableEntityConfiguration<HelpTopicTranslation>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<HelpTopicTranslation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("HelpTopicTranslations");

        builder.Property(t => t.HelpTopicId).IsRequired();
        builder.Property(t => t.Language).IsRequired().HasMaxLength(8);
        builder.Property(t => t.Title).IsRequired().HasMaxLength(200);
        builder.Property(t => t.BodyMarkdown).IsRequired().HasMaxLength(20_000);
        builder.Property(t => t.IsApproved).IsRequired().HasDefaultValue(false);
        builder.Property(t => t.TranslatorNote).HasMaxLength(1024);

        // Foreign key to HelpTopic.
        builder.HasOne<HelpTopic>()
            .WithMany()
            .HasForeignKey(t => t.HelpTopicId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite natural-key UNIQUE.
        builder.HasIndex(t => new { t.HelpTopicId, t.Language }).IsUnique();
    }
}
