using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1600 / R1406 / TOR Annex 3.8 — maps <see cref="ExecutoryDocument"/> to
/// <c>cnas.ExecutoryDocuments</c>. A unique index on
/// <see cref="ExecutoryDocument.DocumentSeriesNumber"/> enforces the natural-key
/// rule (every document carries a stable external identifier). Secondary
/// indexes on <c>(DebtorIdnpHash, Status, PriorityRank)</c> and
/// <c>(Status, EffectiveFrom)</c> back the withholding-calculator lookups and
/// the operator's active-documents report.
/// </summary>
/// <remarks>
/// <para>
/// The plaintext <see cref="ExecutoryDocument.DebtorIdnp"/> and
/// <see cref="ExecutoryDocument.CreditorAccountIban"/> columns are wired into
/// the <c>EncryptedStringConverter</c> by <c>CnasDbContext.OnModelCreating</c>;
/// the configuration here only sets the column length and required-ness.
/// </para>
/// <para>
/// Enum columns persist as stable enum-name strings (mirrors the established
/// pattern for <see cref="ExecutoryDocumentKind"/>,
/// <see cref="ExecutoryDocumentStatus"/>,
/// <see cref="ExecutoryDocumentWithholdingMode"/>) so that humans can read the
/// rows directly and the persistence contract is decoupled from the underlying
/// integer values.
/// </para>
/// </remarks>
public sealed class ExecutoryDocumentConfiguration : AuditableEntityConfiguration<ExecutoryDocument>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<ExecutoryDocument> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ExecutoryDocuments");

        builder.Property(e => e.DocumentSeriesNumber).IsRequired().HasMaxLength(32);

        // DebtorIdnp ciphertext: cap generously at 512 to leave head-room for
        // the AES envelope (~96 chars for a 13-char IDNP).
        builder.Property(e => e.DebtorIdnp).IsRequired().HasMaxLength(512);
        builder.Property(e => e.DebtorIdnpHash).IsRequired().HasMaxLength(64);

        builder.Property(e => e.Kind)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        builder.Property(e => e.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        builder.Property(e => e.WithholdingMode)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(e => e.IssuedBy).IsRequired().HasMaxLength(256);
        builder.Property(e => e.IssuedDate).IsRequired();
        builder.Property(e => e.EffectiveFrom).IsRequired();
        builder.Property(e => e.EffectiveUntil);

        builder.Property(e => e.WithholdingAmountMdl).HasPrecision(18, 2);
        builder.Property(e => e.WithholdingPercentage).HasPrecision(5, 2);
        builder.Property(e => e.PriorityRank).IsRequired();

        // CreditorAccountIban ciphertext: same head-room as the IDNP column.
        builder.Property(e => e.CreditorAccountIban).IsRequired().HasMaxLength(512);
        builder.Property(e => e.CreditorAccountIbanHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.CreditorName).IsRequired().HasMaxLength(256);

        builder.Property(e => e.TotalOwedMdl).HasPrecision(18, 2);
        builder.Property(e => e.TotalWithheldMdl).HasPrecision(18, 2).IsRequired();
        builder.Property(e => e.RegisteredByUserId).IsRequired();

        builder.Property(e => e.CompletedDate);
        builder.Property(e => e.CancellationReason).HasMaxLength(500);

        // Natural-key uniqueness — one document per external identifier.
        builder.HasIndex(e => e.DocumentSeriesNumber)
            .IsUnique()
            .HasDatabaseName("UX_ExecutoryDocuments_DocumentSeriesNumber");

        // Calculator lookup index — pulls Active rows for a debtor in priority
        // order. Composite to keep the leading-column equality + range scan on
        // a single index without an extra sort.
        builder.HasIndex(e => new { e.DebtorIdnpHash, e.Status, e.PriorityRank })
            .HasDatabaseName("IX_ExecutoryDocuments_Debtor_Status_Priority");

        // Operator report index — "all active documents in effect after a
        // given date" surfaces the active-registry overview without scanning
        // the entire table.
        builder.HasIndex(e => new { e.Status, e.EffectiveFrom })
            .HasDatabaseName("IX_ExecutoryDocuments_Status_EffectiveFrom");
    }
}
