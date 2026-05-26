using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0803 — maps <see cref="PayerBankAccount"/> to <c>cnas.PayerBankAccounts</c>. Carries
/// the two filtered unique indexes that back the BP 1.1-D invariants:
/// <c>UX_PayerBankAccounts_CurrentPrimary</c> (at most one current primary row per
/// Payer) and <c>UX_PayerBankAccounts_CurrentIban</c> (the same IBAN hash may not
/// appear on two open rows for the same Payer). The InMemory test provider ignores
/// partial-index filters; the service layer enforces the invariants programmatically
/// in that environment.
/// </summary>
/// <remarks>
/// The plaintext <see cref="PayerBankAccount.Iban"/> column is wired into the
/// <c>EncryptedStringConverter</c> by <c>CnasDbContext.OnModelCreating</c> — the
/// configuration here only sets the column length and required-ness; the converter
/// wraps the column transparently when an <c>IFieldEncryptor</c> is registered.
/// </remarks>
public sealed class PayerBankAccountConfiguration : AuditableEntityConfiguration<PayerBankAccount>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PayerBankAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PayerBankAccounts");

        builder.Property(e => e.PayerId).IsRequired();
        builder.Property(e => e.AccountHolderName).IsRequired().HasMaxLength(200);
        // Iban ciphertext: width is provider-dependent; cap generously to 512 chars
        // so the AES envelope (~96 chars for a 34-char IBAN) leaves head-room.
        builder.Property(e => e.Iban).IsRequired().HasMaxLength(512);
        builder.Property(e => e.IbanHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.BankName).IsRequired().HasMaxLength(200);
        builder.Property(e => e.BankBic).IsRequired().HasMaxLength(11);
        builder.Property(e => e.IsPrimary).IsRequired();
        builder.Property(e => e.Currency).IsRequired().HasMaxLength(3).HasDefaultValue("MDL");
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => e.PayerId);
        builder.HasIndex(e => e.ValidFromUtc);
        builder.HasIndex(e => e.IbanHash);

        // R0803 — at most one current primary bank account per Payer.
        builder.HasIndex(e => e.PayerId)
            .HasFilter("\"ValidToUtc\" IS NULL AND \"IsPrimary\" = TRUE")
            .IsUnique()
            .HasDatabaseName("UX_PayerBankAccounts_CurrentPrimary");

        // R0803 — the same canonicalised IBAN may not appear on two open rows for the
        // same Payer (prevents duplicates regardless of primary/non-primary flag).
        builder.HasIndex(e => new { e.PayerId, e.IbanHash })
            .HasFilter("\"ValidToUtc\" IS NULL")
            .IsUnique()
            .HasDatabaseName("UX_PayerBankAccounts_CurrentIban");
    }
}
