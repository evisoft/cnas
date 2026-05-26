using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="HelpTopic"/> to <c>cnas.HelpTopics</c> — the contextual-help
/// registry consulted by the per-page help widget (R0225 / TOR UI 015).
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural key.</b> Unique on <see cref="HelpTopic.Code"/> — the runtime resolver
/// addresses topics by their stable kebab-case code.
/// </para>
/// <para>
/// <b>Module index.</b> Index on <see cref="HelpTopic.Module"/> for the admin
/// "show topics in module X" listing.
/// </para>
/// <para>
/// <b>Column caps.</b> 128-char <see cref="HelpTopic.Code"/>, 64-char
/// <see cref="HelpTopic.Module"/>, 256-char
/// <see cref="HelpTopic.AnchorSelector"/>.
/// </para>
/// </remarks>
public sealed class HelpTopicConfiguration : AuditableEntityConfiguration<HelpTopic>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<HelpTopic> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("HelpTopics");

        builder.Property(t => t.Code).IsRequired().HasMaxLength(128);
        builder.Property(t => t.Module).IsRequired().HasMaxLength(64);
        builder.Property(t => t.AnchorSelector).HasMaxLength(256);

        // Natural-key UNIQUE on Code.
        builder.HasIndex(t => t.Code).IsUnique();

        // Module index for filtered listing.
        builder.HasIndex(t => t.Module);
    }
}
