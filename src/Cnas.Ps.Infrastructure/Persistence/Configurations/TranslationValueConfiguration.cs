using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="TranslationValue"/> to <c>cnas.TranslationValues</c> — the
/// per-language localised text rows for <see cref="TranslationKey"/> (R0210 / TOR
/// UI 007 / CF 17.16).
/// </summary>
/// <remarks>
/// <para>
/// <b>Natural key.</b> Composite UNIQUE on
/// (<see cref="TranslationValue.TranslationKeyId"/>, <see cref="TranslationValue.Language"/>)
/// — one row per (key, language) pair. The upsert service applies this invariant
/// idempotently; the database-level uniqueness is the safety net.
/// </para>
/// <para>
/// <b>Foreign key.</b> Cascade-delete from <see cref="TranslationKey"/> via the
/// implicit shadow FK on <see cref="TranslationValue.TranslationKeyId"/>: removing a
/// key removes its values (hard delete is unusual — operators usually flip
/// <see cref="AuditableEntity.IsActive"/>).
/// </para>
/// <para>
/// <b>Column caps.</b> 8-char <see cref="TranslationValue.Language"/>, 2000-char
/// <see cref="TranslationValue.Text"/>, 1024-char
/// <see cref="TranslationValue.TranslatorNote"/>.
/// </para>
/// </remarks>
public sealed class TranslationValueConfiguration : AuditableEntityConfiguration<TranslationValue>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<TranslationValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TranslationValues");

        builder.Property(v => v.TranslationKeyId).IsRequired();
        builder.Property(v => v.Language).IsRequired().HasMaxLength(8);
        builder.Property(v => v.Text).IsRequired().HasMaxLength(2000);
        builder.Property(v => v.IsApproved).IsRequired().HasDefaultValue(false);
        builder.Property(v => v.TranslatorNote).HasMaxLength(1024);

        // Foreign key to TranslationKey.
        builder.HasOne<TranslationKey>()
            .WithMany()
            .HasForeignKey(v => v.TranslationKeyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite natural-key UNIQUE: one row per (key, language).
        builder.HasIndex(v => new { v.TranslationKeyId, v.Language }).IsUnique();
    }
}
