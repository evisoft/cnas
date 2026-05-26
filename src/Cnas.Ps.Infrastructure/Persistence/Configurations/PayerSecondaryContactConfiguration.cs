using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0803 — maps <see cref="PayerSecondaryContact"/> to <c>cnas.PayerSecondaryContacts</c>.
/// No filtered-unique-current-row constraint: a Payer may carry multiple concurrent
/// secondary contacts (Accountant, Legal, etc.). The primary contact lives on
/// <see cref="PayerContact"/> (R0301) — see that entity's configuration for the
/// single-current-row invariant.
/// </summary>
public sealed class PayerSecondaryContactConfiguration : AuditableEntityConfiguration<PayerSecondaryContact>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PayerSecondaryContact> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PayerSecondaryContacts");

        builder.Property(e => e.PayerId).IsRequired();
        builder.Property(e => e.ContactPersonName).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Role).HasMaxLength(100);
        builder.Property(e => e.PhoneE164).HasMaxLength(32);
        builder.Property(e => e.Email).HasMaxLength(254);
        builder.Property(e => e.ValidFromUtc).IsRequired();
        builder.Property(e => e.ChangeReason).HasMaxLength(500);
        builder.Property(e => e.RecordedByUserSqid).HasMaxLength(64);

        builder.HasIndex(e => e.PayerId);
        builder.HasIndex(e => e.ValidFromUtc);
    }
}
