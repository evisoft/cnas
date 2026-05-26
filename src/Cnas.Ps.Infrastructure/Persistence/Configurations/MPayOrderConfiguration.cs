using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cnas.Ps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="MPayOrder"/> to <c>cnas.MPayOrders</c> — the persisted record of MPay
/// payment orders originated by CNAS. Two domain-specific indexes are declared in
/// addition to the soft-delete + audit-timestamp indexes contributed by
/// <see cref="AuditableEntityConfiguration{TEntity}"/>:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>UNIQUE (OrderId)</c> — the natural key. MPay refers to the CNAS-side order
///       identifier on every callback, and the service layer relies on the unique
///       constraint to detect double-insert programming errors.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>(OrderId, PaymentRef)</c> — composite lookup index supporting the
///       idempotency guard in <c>MPayOrderStore.ConfirmAsync</c>. Together with the
///       natural-key uniqueness above this is enough to make a retried confirmation
///       with the same payment reference a no-op success, and to detect a conflicting
///       retry with a different payment reference as <see cref="Cnas.Ps.Core.Common.ErrorCodes.Conflict"/>.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Column widths.</b>
/// <list type="bullet">
///   <item><description><c>OrderId</c> — <c>varchar(64)</c> (CNAS-side ids fit comfortably).</description></item>
///   <item><description><c>DescriptionRo</c> — <c>varchar(512)</c> (single-line descriptor shown on the MPay page).</description></item>
///   <item><description><c>BeneficiaryIdnp</c> — <c>varchar(128)</c> (matches the existing IDNP column widths on <see cref="InsuredPerson.Idnp"/> / <see cref="Solicitant.NationalId"/>, leaving headroom for the future encryption-converter ciphertext).</description></item>
///   <item><description><c>PaymentRef</c> — <c>varchar(128)</c> (upstream bank/processor transaction ids are typically short).</description></item>
///   <item><description><c>AmountMdl</c> — <c>decimal(18, 2)</c> (canonical financial precision in the schema).</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class MPayOrderConfiguration : AuditableEntityConfiguration<MPayOrder>
{
    /// <inheritdoc />
    protected override void ConfigureEntity(EntityTypeBuilder<MPayOrder> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MPayOrders");

        builder.Property(o => o.OrderId).IsRequired().HasMaxLength(64);
        builder.Property(o => o.AmountMdl).IsRequired().HasColumnType("decimal(18,2)");
        builder.Property(o => o.DescriptionRo).IsRequired().HasMaxLength(512);

        // BeneficiaryIdnp width matches the existing encrypted IDNP columns
        // (Solicitant.NationalId, InsuredPerson.Idnp, UserProfile.NationalId — all
        // varchar(128)) so the future encryption-converter wiring lands without a
        // column-width migration. See MPayOrder.BeneficiaryIdnp XML doc.
        builder.Property(o => o.BeneficiaryIdnp).IsRequired().HasMaxLength(128);

        // PaymentRef carries an explicit optimistic-concurrency token so two
        // concurrent ConfirmAsync callbacks with different paymentRef values
        // cannot both overwrite the same row. The natural race is "MPay
        // delivers the callback twice (or a duplicate session retries) with
        // different refs" — the loser's UPDATE statement sees
        // DbUpdateConcurrencyException, which MPayOrderStore.ConfirmAsync
        // catches and translates into a structured Conflict (or idempotent
        // success when the ref matches the persisted value). The xmin token
        // contributed by AuditableEntityConfiguration is not enough on its
        // own because a Postgres UPDATE that only touches PaymentRef and
        // related cols on a fresh row needs the column to be tracked as
        // changed for the xmin compare to participate — IsConcurrencyToken
        // makes the comparison explicit and provider-portable.
        builder.Property(o => o.PaymentRef).HasMaxLength(128).IsConcurrencyToken();
        builder.Property(o => o.ConfirmedAtUtc);

        // R1504 / TOR §3.7-E — suspension flag + timestamp. Indexed via the
        // composite (IsSuspended, ConfirmedAtUtc) below so the dispatcher's
        // "find pending unsuspended orders" hot path stays sargable.
        builder.Property(o => o.IsSuspended).IsRequired();
        builder.Property(o => o.SuspendedAtUtc);

        // Natural key. MPay refers to OrderId on every callback; a duplicate insert is a
        // programming error in the service layer — the unique constraint surfaces it as
        // a deterministic DbUpdateException at SaveChanges time.
        builder.HasIndex(o => o.OrderId).IsUnique();

        // Composite index supporting the idempotency guard in MPayOrderStore.ConfirmAsync.
        // PaymentRef is nullable, so this is a regular (non-unique) lookup index — the
        // service layer enforces the "no double overwrite" rule programmatically, not via
        // a UNIQUE constraint. We avoid the partial-index syntax used elsewhere (e.g.
        // DocumentTemplateConfiguration) because retried confirmations are expected to
        // match on the FULL (OrderId, PaymentRef) tuple after the first confirm has
        // populated PaymentRef — that's the row the idempotent path needs to find.
        builder.HasIndex(o => new { o.OrderId, o.PaymentRef });
    }
}
