using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// R0516 / TOR CF 02.04 — maps <see cref="PersonalAccount"/> to
/// <c>cnas.PersonalAccounts</c>. The surrogate <see cref="AuditableEntity.Id"/>
/// is the primary key; <see cref="PersonalAccount.AccountCode"/> is a unique
/// natural-key string used by the citizen-facing portal. A secondary unique
/// index on <see cref="PersonalAccount.OwnerSolicitantId"/> enforces the
/// "one account per Solicitant" rule documented on the aggregate.
/// </summary>
public sealed class PersonalAccountConfiguration : AuditableEntityConfiguration<PersonalAccount>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<PersonalAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PersonalAccounts");

        builder.Property(p => p.AccountCode).IsRequired().HasMaxLength(64);
        builder.Property(p => p.LifetimeContributions).HasPrecision(18, 2);
        builder.Property(p => p.LifetimeMonths);
        builder.Property(p => p.OwnerSolicitantId).IsRequired();

        // Stable external code — unique even across soft-deleted rows because
        // codes are never recycled (CLAUDE.md cross-cutting "Soft Deletes").
        builder.HasIndex(p => p.AccountCode).IsUnique();

        // One personal account per Solicitant — enforced by a unique index on
        // the FK column. Soft-deleted accounts still hold their slot; the
        // application layer reactivates rather than recreates.
        builder.HasIndex(p => p.OwnerSolicitantId).IsUnique();
    }
}
