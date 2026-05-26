namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0516 / TOR CF 02.04 — citizen-facing "Cont personal" aggregate. Acts as the
/// container for the chronological list of contribution entries
/// (<see cref="PersonalAccountEntry"/>) attributed to one
/// <see cref="Solicitant"/>. Surfaces a stable, opaque
/// <see cref="AccountCode"/> as the external identifier (shape compatible with
/// the placeholder code synthesized by
/// <c>Cnas.Ps.Infrastructure.Services.PublicServices.ExtractCnasCodeService</c>)
/// plus two cached counters used by dashboards: lifetime contribution
/// amount and the count of distinct contribution months.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate aggregate.</b> <see cref="InsuredPerson"/> tracks
/// CNAS-side identity sourced from RSP (national id, name, DOB) — it is not the
/// right home for application-layer financial roll-ups. The PersonalAccount
/// row is the citizen's CNAS "wallet" handle, owned by the Solicitant who is
/// linked into the user-directory (the same person authenticated via MPass /
/// local credentials). Reports and the citizen self-service extract aggregate
/// from this aggregate, never from <see cref="InsuredPerson"/>.
/// </para>
/// <para>
/// <b>External id.</b> <see cref="AccountCode"/> is a hand-curated stable
/// string (typically <c>"PA-XXXX"</c>). It is part of the public contract and
/// therefore does not undergo Sqid round-tripping (CLAUDE.md RULE 3 is
/// satisfied — the surrogate <c>Id</c> never crosses the boundary). The
/// <see cref="IExternalId"/> marker is still set because external DTOs surface
/// the Sqid of the owning <see cref="Solicitant"/> + the account code, and
/// future foreign-key references (e.g. payment dispositions) may carry the
/// account's Sqid alongside.
/// </para>
/// <para>
/// <b>Lifetime counters.</b> The two cache fields (<see cref="LifetimeContributions"/>
/// and <see cref="LifetimeMonths"/>) are projection caches recomputed by the
/// application layer whenever new entries land. They are eventually consistent
/// with the underlying entries — never the source of truth for billing,
/// pension-amount math, or audit. Treat as read-mostly metadata.
/// </para>
/// </remarks>
public sealed class PersonalAccount : AuditableEntity, IExternalId
{
    /// <summary>
    /// Internal foreign-key reference to the owning <see cref="Solicitant"/>.
    /// One Solicitant has at most one active personal account; the application
    /// layer enforces this via the unique index defined in
    /// <c>PersonalAccountConfiguration</c>.
    /// </summary>
    public long OwnerSolicitantId { get; set; }

    /// <summary>
    /// Stable opaque external identifier (typically <c>"PA-XXXX"</c>). Used by
    /// the citizen-facing portal, R0513 anonymous lookup, and inter-system
    /// references. Never recycled — soft-deleted accounts retain their code.
    /// Distinct from the surrogate <see cref="AuditableEntity.Id"/>, which
    /// stays internal per CLAUDE.md RULE 3.
    /// </summary>
    public required string AccountCode { get; set; }

    /// <summary>
    /// Sum of every <see cref="PersonalAccountEntry.ContributionPaidAmount"/>
    /// rolled up across the account's lifetime. Cached projection — the
    /// application layer recomputes after every insert / soft-delete and stores
    /// the result here. Currency is implicitly Moldovan leu (MDL).
    /// </summary>
    public decimal LifetimeContributions { get; set; }

    /// <summary>
    /// Count of distinct (Year, Month) buckets that hold at least one
    /// contribution entry for this account. Backs the "ani de stagiu" / months
    /// of service display on the personal-account extract; the application
    /// layer recomputes this alongside <see cref="LifetimeContributions"/>.
    /// </summary>
    public int LifetimeMonths { get; set; }
}
