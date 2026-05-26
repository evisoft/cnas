namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0910 / TOR BP 2.2-A — one insured-person-level breakdown row inside a
/// <see cref="Rev5Declaration"/> header. Each row attributes a slice of the
/// employer's monthly contribution to one identified insured person via the
/// IDNP hash, capturing the contribution-base (gross salary subject to
/// contribution), the contribution amount itself, and optional context.
/// </summary>
/// <remarks>
/// <para>
/// <b>IDNP lookup via hash.</b> The row never carries the plaintext IDNP. It
/// stores the deterministic HMAC-SHA256 hash (per
/// <c>IDeterministicHasher</c>) so the service layer can resolve it against
/// <see cref="Solicitant.NationalIdHash"/>. When the hash does not resolve to
/// a known Solicitant the row is still persisted (rejecting whole declarations
/// because of one unknown person would lose the legitimate other-row data) —
/// the unmatched count surfaces on the response so operators can chase the
/// missing CNAS registry record.
/// </para>
/// <para>
/// <b>Natural-key uniqueness.</b>
/// <c>(Rev5DeclarationId, InsuredPersonNationalIdHash)</c> is unique — the
/// same employee cannot appear twice in the same REV-5 declaration. Enforced
/// via a composite unique index in
/// <c>Cnas.Ps.Infrastructure.Persistence.Configurations.Rev5DeclarationRowConfiguration</c>.
/// </para>
/// <para>
/// <b>Cascade delete.</b> The FK to the parent <see cref="Rev5Declaration"/>
/// is configured with cascade delete in the EF mapping. Cancellation of the
/// parent is modeled as a status transition rather than a hard delete;
/// cascade delete is a safety net for ad-hoc admin cleanup only.
/// </para>
/// </remarks>
public sealed class Rev5DeclarationRow : AuditableEntity, IExternalId
{
    /// <summary>
    /// Foreign-key reference to the owning <see cref="Rev5Declaration"/>.
    /// Cascade-delete configured in EF.
    /// </summary>
    public long Rev5DeclarationId { get; set; }

    /// <summary>
    /// Deterministic HMAC-SHA256 hash of the insured person's IDNP — matches
    /// the same hashing contract as <see cref="Solicitant.NationalIdHash"/>
    /// so the service layer can perform a direct equality lookup. The
    /// plaintext IDNP never crosses the boundary into this entity. 1..128
    /// chars enforced by the validator.
    /// </summary>
    public required string InsuredPersonNationalIdHash { get; set; }

    /// <summary>
    /// Gross salary subject to contribution for the month (MDL). Distinct
    /// from <see cref="ContributionAmount"/> because the calculator's
    /// "average monthly contribution base" projects this column, not the
    /// paid amount.
    /// </summary>
    public decimal ContributionBaseAmount { get; set; }

    /// <summary>
    /// Contribution paid for this insured person (MDL). Aggregated by the
    /// parent header's <see cref="Rev5Declaration.TotalDeclaredAmount"/> and
    /// projected into the citizen's
    /// <see cref="PersonalAccountEntry.ContributionPaidAmount"/>.
    /// </summary>
    public decimal ContributionAmount { get; set; }

    /// <summary>
    /// Number of days the insured person worked during the reporting month
    /// (0..31 enforced by the validator). Optional context — null when the
    /// employer's upstream system did not capture it.
    /// </summary>
    public int? DaysWorked { get; set; }

    /// <summary>
    /// Optional position / job-code from the employer's HR system (≤ 64
    /// chars when set). Stored for diagnostic visibility; not used in any
    /// calculations.
    /// </summary>
    public string? PositionCode { get; set; }
}
