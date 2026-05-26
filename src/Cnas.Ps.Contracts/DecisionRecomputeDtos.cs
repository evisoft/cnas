using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R1502 / TOR §3.7-C — canonical reason for a benefit-decision recompute.
/// Drives the audit-trail bucketing and the picker on the recompute admin UI.
/// </summary>
/// <remarks>
/// Stable enum-name strings cross the wire (the DTO serialises by name). Append
/// new values at the end of the enum to keep the wire contract additive.
/// </remarks>
public enum DecisionRecomputeReason
{
    /// <summary>The pension / benefit calculation base changed (e.g. average insured income revised).</summary>
    BaseAmountChanged = 0,

    /// <summary>A previously-recorded payment was reversed and the period must be re-billed.</summary>
    PaymentReversed = 1,

    /// <summary>A legislative or regulatory change altered the calculation parameters.</summary>
    LegislativeUpdate = 2,

    /// <summary>Catch-all for recomputes that do not fit a specific reason.</summary>
    Other = 99,
}

/// <summary>
/// R1502 / TOR §3.7-C — outcome of a successful
/// <c>IDecisionRecomputeService.RecomputeAsync</c> invocation.
/// </summary>
/// <param name="PriorAmount">Amount on the prior decision in MDL (null when unknown).</param>
/// <param name="NewAmount">Amount on the recompute result in MDL.</param>
/// <param name="Delta">Signed difference <c>NewAmount − PriorAmount</c> in MDL.</param>
/// <param name="NewDocumentSqid">
/// Sqid-encoded id of the newly-generated <c>Document</c> row carrying the
/// recompute decision (either an adjustment or a recuperare). <c>null</c>
/// when <see cref="Delta"/> is zero and no new document was issued.
/// </param>
/// <param name="DocumentKindCode">
/// Stable code identifying which template was emitted:
/// <c>"DECIZIE_AJUSTARE_SUME"</c> for a positive delta,
/// <c>"DECIZIE_RECUPERARE_SUME"</c> for a negative delta,
/// <c>"NO_CHANGE"</c> when the delta is zero.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record DecisionRecomputeOutcomeDto(
    decimal? PriorAmount,
    decimal NewAmount,
    decimal Delta,
    string? NewDocumentSqid,
    string DocumentKindCode);
