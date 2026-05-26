using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1201 / R1402 / TOR §3.4-B / §3.6-C — maps
/// <see cref="IntlAgreementReviewCase"/> to
/// <c>cnas.IntlAgreementReviewCases</c>. A unique index on
/// <see cref="IntlAgreementReviewCase.CaseNumber"/> enforces the natural-key
/// rule. A filtered unique index on
/// <c>(BeneficiaryIdnpHash, AgreementCode, BenefitKind)</c> (filtered to
/// non-terminal statuses) prevents two concurrent active routing cases for
/// the same beneficiary + agreement + benefit kind.
/// </summary>
/// <remarks>
/// <para>
/// The plaintext <see cref="IntlAgreementReviewCase.BeneficiaryIdnp"/>
/// column is wired into <c>EncryptedStringConverter</c> by
/// <c>CnasDbContext.OnModelCreating</c>; this configuration only sets
/// column length and required-ness.
/// </para>
/// <para>
/// Enum columns persist as stable enum-name strings (mirrors the
/// established pattern for the rest of the codebase) so humans can read
/// the rows directly.
/// </para>
/// </remarks>
public sealed class IntlAgreementReviewCaseConfiguration
    : AuditableEntityConfiguration<IntlAgreementReviewCase>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<IntlAgreementReviewCase> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IntlAgreementReviewCases");

        builder.Property(e => e.CaseNumber).IsRequired().HasMaxLength(32);

        // BeneficiaryIdnp ciphertext: cap generously at 512 to leave head-room
        // for the AES envelope (~96 chars for a 13-char IDNP).
        builder.Property(e => e.BeneficiaryIdnp).IsRequired().HasMaxLength(512);
        builder.Property(e => e.BeneficiaryIdnpHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.BeneficiaryDisplayName).IsRequired().HasMaxLength(256);

        builder.Property(e => e.BenefitKind)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(e => e.AgreementCode).IsRequired().HasMaxLength(64);
        builder.Property(e => e.HostCountryCode).IsRequired().HasMaxLength(2);

        builder.Property(e => e.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(e => e.CurrentLevel)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(e => e.ReferenceBenefitPassportSqid).HasMaxLength(32);

        builder.Property(e => e.SubmittedAt);
        builder.Property(e => e.ApprovedAt);
        builder.Property(e => e.RejectedAt);
        builder.Property(e => e.RejectionReason).HasMaxLength(1000);
        builder.Property(e => e.RevisionRequestedAt);
        builder.Property(e => e.RevisionRequestNote).HasMaxLength(1000);
        builder.Property(e => e.CancelledAt);
        builder.Property(e => e.CancelReason).HasMaxLength(1000);

        builder.Property(e => e.RegisteredByUserId).IsRequired();
        builder.Property(e => e.EvidenceJson).HasMaxLength(16384);

        // Natural-key uniqueness — one case per external identifier.
        builder.HasIndex(e => e.CaseNumber)
            .IsUnique()
            .HasDatabaseName("UX_IntlAgreementReviewCases_CaseNumber");

        // Operator-list index — "all rows at a given status + current level".
        builder.HasIndex(e => new { e.Status, e.CurrentLevel })
            .HasDatabaseName("IX_IntlAgreementReviewCases_Status_CurrentLevel");

        // Cross-benefit-kind operator index — "all open rows for a benefit".
        builder.HasIndex(e => new { e.BenefitKind, e.Status })
            .HasDatabaseName("IX_IntlAgreementReviewCases_BenefitKind_Status");

        // Look-up by beneficiary hash (e.g. "does the beneficiary already have an open case?").
        builder.HasIndex(e => e.BeneficiaryIdnpHash)
            .HasDatabaseName("IX_IntlAgreementReviewCases_BeneficiaryIdnpHash");

        // Filtered uniqueness — prevent two concurrent non-terminal cases
        // for the same beneficiary + agreement + benefit kind. Terminal
        // Approved / Rejected / Cancelled rows are allowed to co-exist with
        // a fresh case (a beneficiary can have a second claim later under
        // the same agreement).
        builder.HasIndex(e => new { e.BeneficiaryIdnpHash, e.AgreementCode, e.BenefitKind })
            .IsUnique()
            .HasFilter("\"Status\" NOT IN ('Approved', 'Rejected', 'Cancelled')")
            .HasDatabaseName("UX_IntlAgreementReviewCases_Beneficiary_Agreement_Active");
    }
}
