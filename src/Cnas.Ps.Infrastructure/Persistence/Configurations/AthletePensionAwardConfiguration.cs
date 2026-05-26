using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R1403 / TOR §3.6-D — maps <see cref="AthletePensionAward"/> to
/// <c>cnas.AthletePensionAwards</c>. A unique index on
/// <see cref="AthletePensionAward.AwardNumber"/> enforces the natural-key
/// rule. A filtered unique index on
/// <c>(BeneficiaryIdnpHash, Role)</c> (filtered to non-terminal status)
/// prevents two concurrent active awards for the same person + role.
/// </summary>
/// <remarks>
/// <para>
/// The plaintext <see cref="AthletePensionAward.BeneficiaryIdnp"/> column
/// is wired into the <c>EncryptedStringConverter</c> by
/// <c>CnasDbContext.OnModelCreating</c>; the configuration here only sets
/// column length and required-ness.
/// </para>
/// <para>
/// Enum columns persist as stable enum-name strings (mirrors the
/// established pattern for the rest of the codebase) so humans can read
/// the rows directly.
/// </para>
/// </remarks>
public sealed class AthletePensionAwardConfiguration
    : AuditableEntityConfiguration<AthletePensionAward>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<AthletePensionAward> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AthletePensionAwards");

        builder.Property(e => e.AwardNumber).IsRequired().HasMaxLength(32);

        // BeneficiaryIdnp ciphertext: cap generously at 512 to leave head-room
        // for the AES envelope (~96 chars for a 13-char IDNP).
        builder.Property(e => e.BeneficiaryIdnp).IsRequired().HasMaxLength(512);
        builder.Property(e => e.BeneficiaryIdnpHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.BeneficiaryDisplayName).IsRequired().HasMaxLength(256);

        builder.Property(e => e.BeneficiaryBirthDate).IsRequired();
        builder.Property(e => e.BeneficiarySex)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();

        builder.Property(e => e.Role)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion<string>();

        builder.Property(e => e.SportDiscipline).IsRequired().HasMaxLength(128);

        builder.Property(e => e.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        builder.Property(e => e.RequestedAt).IsRequired();
        builder.Property(e => e.ApprovedAt);
        builder.Property(e => e.RejectedAt);
        builder.Property(e => e.RejectionReason).HasMaxLength(1000);
        builder.Property(e => e.EffectiveFrom);
        builder.Property(e => e.SuspendedAt);
        builder.Property(e => e.SuspensionReason).HasMaxLength(1000);
        builder.Property(e => e.TerminatedAt);
        builder.Property(e => e.TerminationReason).HasMaxLength(1000);

        builder.Property(e => e.MonthlyAmountMdl).HasPrecision(18, 2);
        builder.Property(e => e.RegulatoryBaseMdl).HasPrecision(18, 2);
        builder.Property(e => e.MultiplierPercent).HasPrecision(10, 2);
        builder.Property(e => e.EligibilityNotesJson).HasMaxLength(32768);

        builder.Property(e => e.RegisteredByUserId).IsRequired();
        builder.Property(e => e.LastRecomputedAt);

        // Natural-key uniqueness — one award per external identifier.
        builder.HasIndex(e => e.AwardNumber)
            .IsUnique()
            .HasDatabaseName("UX_AthletePensionAwards_AwardNumber");

        // Operator-list index — "all rows for a given status + effective-from".
        builder.HasIndex(e => new { e.Status, e.EffectiveFrom })
            .HasDatabaseName("IX_AthletePensionAwards_Status_EffectiveFrom");

        // Look-up by beneficiary hash (e.g. "do they already have an award?").
        builder.HasIndex(e => e.BeneficiaryIdnpHash)
            .HasDatabaseName("IX_AthletePensionAwards_BeneficiaryIdnpHash");

        // Filtered uniqueness — prevent two concurrent active awards for the
        // same person + role. Terminal Rejected / Terminated rows are allowed
        // to co-exist with a fresh award.
        builder.HasIndex(e => new { e.BeneficiaryIdnpHash, e.Role })
            .IsUnique()
            .HasFilter("\"Status\" NOT IN ('Rejected', 'Terminated')")
            .HasDatabaseName("UX_AthletePensionAwards_Beneficiary_Role_Active");
    }
}
