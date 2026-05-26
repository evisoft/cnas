using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="TranslationKey"/> to <c>cnas.TranslationKeys</c> — the i18n key
/// registry consulted by the translation resolver and rendered by the admin tool
/// (R0210 / TOR UI 007 / CF 17.16).
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural key.</b> Unique on <see cref="TranslationKey.Code"/> — the resolver
/// looks rows up by code at runtime; the database-level uniqueness is the safety
/// net against duplicate inserts.
/// </para>
/// <para>
/// <b>Module index.</b> Filtered index on <see cref="TranslationKey.Module"/> so the
/// admin list "show every key in module X" stays index-served.
/// </para>
/// <para>
/// <b>Column caps.</b> 128-char <see cref="TranslationKey.Code"/>, 1024-char
/// <see cref="TranslationKey.Description"/>, 64-char <see cref="TranslationKey.Module"/>.
/// </para>
/// </remarks>
public sealed class TranslationKeyConfiguration : AuditableEntityConfiguration<TranslationKey>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<TranslationKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TranslationKeys");

        builder.Property(k => k.Code).IsRequired().HasMaxLength(128);
        builder.Property(k => k.Description).HasMaxLength(1024);
        builder.Property(k => k.Module).HasMaxLength(64);

        // Natural-key UNIQUE on Code — one row per stable key.
        builder.HasIndex(k => k.Code).IsUnique();

        // Module index so module-filtered listing stays cheap.
        builder.HasIndex(k => k.Module);
    }
}
