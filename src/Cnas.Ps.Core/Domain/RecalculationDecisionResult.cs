namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1503 / TOR §3.7-D — per-decision outcome row inside a
/// <see cref="RecalculationRun"/>. Carries the old amount, the new amount,
/// the delta, the operator-facing status, and an optional reason. The
/// orchestrator never writes plaintext IDNP — beneficiary identification is
/// via the HMAC IDNP hash so the row can be located without leaking PII.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.RecalculationDecisionResultDto.Id</c>)
/// carries a Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>Raw <see cref="BenefitDecisionId"/>.</b> The mass-recalculation surface
/// is an INTERNAL ops dashboard. The decision aggregate may not yet exist as
/// a first-class entity in this build — the raw bigint is the only stable
/// pointer back to the underlying row. Operators with direct DB access use
/// it to chase forensics. The companion
/// <see cref="BenefitType"/> string disambiguates the table when the
/// aggregate eventually splits into per-kind ones. This mirrors the iter-76
/// <c>IntegrityCheckFinding.AggregateRowId</c> documented exception to
/// CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>No PII at rest.</b> The HMAC IDNP hash is the only beneficiary
/// reference. <see cref="Reason"/> and <see cref="RecalculationContextJson"/>
/// MUST NOT carry plaintext IDNP / IBAN / full names — strategy code is
/// responsible for redaction before the orchestrator persists the row.
/// </para>
/// </remarks>
public sealed class RecalculationDecisionResult : AuditableEntity, IExternalId
{
    /// <summary>FK to the parent <see cref="RecalculationRun"/>.</summary>
    public long RunId { get; set; }

    /// <summary>Internal id of the benefit-decision row that was recomputed (raw bigint, ops-only).</summary>
    public long BenefitDecisionId { get; set; }

    /// <summary>Stable enum-name of the affected benefit kind (e.g. <c>OldAgePension</c>).</summary>
    public string BenefitType { get; set; } = string.Empty;

    /// <summary>HMAC IDNP hash of the beneficiary (base64, 44 chars) for forensic lookup without PII.</summary>
    public string BeneficiaryIdnpHash { get; set; } = string.Empty;

    /// <summary>Amount payable under the OLD rules in MDL (snapshot at run time).</summary>
    public decimal OldAmountMdl { get; set; }

    /// <summary>Amount payable under the NEW rules in MDL (strategy projection).</summary>
    public decimal NewAmountMdl { get; set; }

    /// <summary>App-maintained convenience field — <c>NewAmountMdl - OldAmountMdl</c>.</summary>
    public decimal DeltaMdl { get; set; }

    /// <summary>Operator-facing status — see <see cref="RecalculationResultStatus"/>.</summary>
    public RecalculationResultStatus Status { get; set; } = RecalculationResultStatus.Computed;

    /// <summary>Operator-facing reason populated when <see cref="Status"/> is non-<c>Computed</c> (≤ 500 chars).</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Strategy-supplied snapshot of the inputs that drove the recomputation
    /// (no PII). Persisted so the post-mortem can reproduce the calculation
    /// without re-reading the source registry.
    /// </summary>
    public string? RecalculationContextJson { get; set; }

    /// <summary>UTC timestamp the row moved to <see cref="RecalculationResultStatus.Applied"/>; null otherwise.</summary>
    public DateTime? AppliedAt { get; set; }
}
