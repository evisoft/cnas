using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1202 / TOR §3.4-C — maps <see cref="CapitalisedPaymentRequest"/> to
/// <c>cnas.CapitalisedPaymentRequests</c>. A unique index on
/// <see cref="CapitalisedPaymentRequest.RequestNumber"/> enforces the natural-
/// key rule (one row per external identifier). A second filtered unique index
/// on <c>(BeneficiaryIdnpHash, LiquidatedDebtorIdnoHash, Status)</c> detects
/// duplicate non-terminal requests for the same beneficiary + debtor pair.
/// </summary>
/// <remarks>
/// <para>
/// The plaintext <see cref="CapitalisedPaymentRequest.BeneficiaryIdnp"/> and
/// <see cref="CapitalisedPaymentRequest.LiquidatedDebtorIdno"/> columns are
/// wired into the <c>EncryptedStringConverter</c> by
/// <c>CnasDbContext.OnModelCreating</c>; the configuration here only sets the
/// column length and required-ness.
/// </para>
/// <para>
/// Enum columns persist as stable enum-name strings (mirrors the established
/// pattern for the rest of the codebase) so humans can read the rows
/// directly and the persistence contract is decoupled from the underlying
/// integer values.
/// </para>
/// </remarks>
public sealed class CapitalisedPaymentRequestConfiguration
    : AuditableEntityConfiguration<CapitalisedPaymentRequest>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<CapitalisedPaymentRequest> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CapitalisedPaymentRequests");

        builder.Property(e => e.RequestNumber).IsRequired().HasMaxLength(32);

        // BeneficiaryIdnp ciphertext: cap generously at 512 to leave head-room
        // for the AES envelope (~96 chars for a 13-char IDNP).
        builder.Property(e => e.BeneficiaryIdnp).IsRequired().HasMaxLength(512);
        builder.Property(e => e.BeneficiaryIdnpHash).IsRequired().HasMaxLength(64);

        builder.Property(e => e.BeneficiaryBirthDate).IsRequired();
        builder.Property(e => e.BeneficiarySex)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();

        builder.Property(e => e.LiquidatedDebtorIdno).IsRequired().HasMaxLength(512);
        builder.Property(e => e.LiquidatedDebtorIdnoHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.LiquidatedDebtorName).IsRequired().HasMaxLength(256);

        builder.Property(e => e.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(e => e.ObligationKind)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(e => e.MonthlyAmountMdl).HasPrecision(18, 2);
        builder.Property(e => e.ObligationStartDate).IsRequired();
        builder.Property(e => e.ObligationEndDate);
        builder.Property(e => e.ValuationDate).IsRequired();
        builder.Property(e => e.LegalDiscountRatePercent).HasPrecision(8, 4);
        builder.Property(e => e.RegisteredByUserId).IsRequired();

        builder.Property(e => e.CancellationReason).HasMaxLength(1000);

        // Natural-key uniqueness — one request per external identifier.
        builder.HasIndex(e => e.RequestNumber)
            .IsUnique()
            .HasDatabaseName("UX_CapitalisedPaymentRequests_RequestNumber");

        // Duplicate-detection: at most one non-terminal row per
        // (beneficiary, debtor, status). The filtered uniqueness keeps
        // historical Cancelled / Rejected / Settled rows from blocking a
        // fresh active request for the same pair.
        builder.HasIndex(e => new
            {
                e.BeneficiaryIdnpHash,
                e.LiquidatedDebtorIdnoHash,
                e.Status,
            })
            .HasDatabaseName("IX_CapitalisedPaymentRequests_Beneficiary_Debtor_Status");

        // Operator-list index — "all active rows for a given status".
        builder.HasIndex(e => new { e.Status, e.ObligationKind })
            .HasDatabaseName("IX_CapitalisedPaymentRequests_Status_Kind");
    }
}
