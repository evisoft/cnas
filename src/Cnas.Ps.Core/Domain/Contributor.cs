namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Plătitor de contribuții — contributor of social-insurance funds to BASS. TOR §2.1 / §2.3 #5.
/// </summary>
/// <remarks>
/// Sourced from RSUD (legal persons) and SFS (declarations) via MConnect. Local persistence
/// is denormalized for fast registry browsing and audit history; authoritative data remains
/// in the upstream registers.
/// </remarks>
public sealed class Contributor : AuditableEntity, IExternalId
{
    /// <summary>IDNP or IDNO (UTF-8 STRING(13)) of the contributor — primary external key.</summary>
    /// <remarks>
    /// <para>
    /// Encrypted at rest via <c>EncryptedStringConverter</c> (CLAUDE.md §5.7 / TOR SEC 035).
    /// Because every encryption samples a fresh nonce, equality lookups against this column
    /// (<c>WHERE Idno == X</c>) cease to work in production; use the
    /// <see cref="IdnoHash"/> shadow column instead — see that property's remarks for the
    /// synchronization contract.
    /// </para>
    /// </remarks>
    public required string Idno { get; set; }

    /// <summary>
    /// Deterministic HMAC-SHA256 of the canonicalized <see cref="Idno"/>. Backs the unique
    /// index and equality lookups that the encrypted plaintext column can no longer support.
    /// Stored as base64 (44 chars).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Synchronization is the application layer's responsibility.</b> Every site that
    /// writes <see cref="Idno"/> MUST also write this column via
    /// <c>Cnas.Ps.Application.Abstractions.IDeterministicHasher.ComputeHash</c> on
    /// the same value. See <c>Solicitant.NationalIdHash</c> remarks for the rationale
    /// (single canonicalization source, no EF interceptor magic).
    /// </para>
    /// <para>
    /// Default is <see cref="string.Empty"/> so test factories that construct
    /// <see cref="Contributor"/> aggregates in-memory without exercising hash-driven
    /// lookups do not have to set it explicitly. Production paths (<c>ContributorService</c>,
    /// MConnect sync jobs) MUST populate it before <c>SaveChanges</c>.
    /// </para>
    /// </remarks>
    public string IdnoHash { get; set; } = string.Empty;

    /// <summary>Display denumire of the contributor.</summary>
    public required string Denumire { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — form of organisation classifier code. Lookup
    /// against a <see cref="Classifier"/> row whose <c>SchemeCode</c> is
    /// <c>"CFOJ"</c> (Clasificatorul formelor de organizare juridică). Optional —
    /// populated by RSUD sync or by paper-registration intake when known. Capped
    /// at 32 chars by the EF configuration.
    /// </summary>
    public string? CfojCode { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — property-form classifier code. Lookup against
    /// a <see cref="Classifier"/> row whose <c>SchemeCode</c> is <c>"CFP"</c>
    /// (Clasificatorul formelor de proprietate). Optional. Capped at 32 chars
    /// by the EF configuration.
    /// </summary>
    public string? CfpCode { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — CAEM economic-activity classifier code. Lookup
    /// against a <see cref="Classifier"/> row whose <c>SchemeCode</c> is
    /// <c>"CAEM_REV2"</c>. Optional. Capped at 32 chars by the EF configuration.
    /// </summary>
    public string? CaemCode { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — UTC instant at which this row's classifier /
    /// status assignment became effective. Optional (older rows ingested before
    /// the column landed leave it null). Read alongside <see cref="ValidToUtc"/>
    /// to determine "what classifier was on file at instant T" without
    /// depending on the audit log alone.
    /// </summary>
    public DateTime? ValidFromUtc { get; set; }

    /// <summary>
    /// R0805 / Annex 1 §8.1.1.6 — UTC instant at which this row's classifier /
    /// status assignment was superseded. <c>null</c> when the assignment is
    /// still current.
    /// </summary>
    public DateTime? ValidToUtc { get; set; }

    /// <summary>True when the contributor is currently listed as insolvent (Registrul pltitorilor insolvabili).</summary>
    public bool IsInsolvent { get; set; }

    /// <summary>UTC date the contributor was first registered in CNAS records.</summary>
    public DateTime RegisteredAtUtc { get; set; }

    /// <summary>UTC date when the contributor was de-registered, if applicable.</summary>
    public DateTime? DeregisteredAtUtc { get; set; }

    /// <summary>Identifier of the upstream record (RSUD entity id) when known.</summary>
    public string? UpstreamRsudId { get; set; }

    /// <summary>
    /// R0305 / BP 1.3 — true when the contributor is administratively deactivated
    /// (suspended business activity, contested registration, etc.). Distinct from
    /// <see cref="AuditableEntity.IsActive"/> (soft-delete / de-registration) so the
    /// row remains visible to lookups and audit history. Deactivated rows are
    /// rejected by mutating operations (BP 1.2 update) until BP 1.4 reactivation.
    /// </summary>
    public bool IsDeactivated { get; set; }

    /// <summary>UTC instant the contributor was administratively deactivated (BP 1.3); null when active.</summary>
    public DateTime? DeactivatedAtUtc { get; set; }

    /// <summary>
    /// Operator-supplied reason captured at BP 1.3 deactivation time and preserved
    /// across BP 1.4 reactivation (cleared on reactivate). Free-form text, 3..500 chars.
    /// </summary>
    public string? DeactivationReason { get; set; }

    /// <summary>
    /// R0305 / BP 1.9 — true when the contributor is a NaturalPerson recorded as deceased.
    /// Terminal state — no reactivation. Set in conjunction with
    /// <see cref="IsDeactivated"/>=true so existing soft-delete filters continue to exclude.
    /// </summary>
    public bool IsDeceased { get; set; }

    /// <summary>UTC instant the contributor was marked deceased (BP 1.9); null otherwise.</summary>
    public DateTime? DeceasedAtUtc { get; set; }

    /// <summary>
    /// R0305 / BP 1.9 — true when the contributor is a LegalPerson recorded as dissolved.
    /// Terminal state — no reactivation. Set in conjunction with
    /// <see cref="IsDeactivated"/>=true so existing soft-delete filters continue to exclude.
    /// </summary>
    public bool IsDissolved { get; set; }

    /// <summary>UTC instant the contributor was marked dissolved (BP 1.9); null otherwise.</summary>
    public DateTime? DissolvedAtUtc { get; set; }

    /// <summary>
    /// R0305 / BP 1.5 — when this row is a duplicate that has been merged into another
    /// surviving contributor, this column holds the survivor's primary key. Null on
    /// every non-duplicate row. Once set, the row is effectively read-only: BP 1.5
    /// also flips <see cref="IsDeactivated"/>=true so mutating operations are rejected.
    /// </summary>
    public long? MergedIntoContributorId { get; set; }

    /// <summary>
    /// R0305 / BP 1.8 — natural code of the CNAS regional branch responsible for this
    /// contributor (e.g. <c>"CNAS-CHIS-CTR"</c>). Bulk reassignment via
    /// <c>ContributorBulkReassignBranchOperation</c> updates this column. <c>null</c>
    /// for legacy / unsigned rows; new registrations leave it null until assigned.
    /// </summary>
    public string? CnasBranchCode { get; set; }
}
