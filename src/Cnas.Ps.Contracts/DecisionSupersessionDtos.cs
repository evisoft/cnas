using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0933 / TOR §10.1 — outbound projection of one
/// <c>Cnas.Ps.Core.Domain.DecisionSupersession</c> append-only row. Wire surface
/// for the terminate-prior-on-acceptance lifecycle.
/// </summary>
/// <remarks>
/// All id fields are Sqid-encoded per CLAUDE.md RULE 3.
/// </remarks>
/// <param name="Id">Sqid-encoded primary key of the supersession row.</param>
/// <param name="PreviousDecisionSqid">
/// Sqid-encoded id of the prior <c>ServiceApplication</c> (decision) that was
/// terminated.
/// </param>
/// <param name="NewDecisionSqid">
/// Sqid-encoded id of the new <c>ServiceApplication</c> (decision) that caused
/// the supersession.
/// </param>
/// <param name="SupersededAtUtc">UTC instant the supersession was recorded.</param>
/// <param name="SupersededByUserSqid">
/// Sqid-encoded id of the user (decider) that triggered the supersession; null
/// for system-initiated supersessions.
/// </param>
/// <param name="Reason">Optional free-text rationale.</param>
/// <param name="PriorAmount">Prior decision's amount snapshot (MDL).</param>
/// <param name="NewAmount">New decision's amount snapshot (MDL).</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record DecisionSupersessionDto(
    string Id,
    string PreviousDecisionSqid,
    string NewDecisionSqid,
    DateTime SupersededAtUtc,
    string? SupersededByUserSqid,
    string? Reason,
    decimal? PriorAmount,
    decimal? NewAmount);

/// <summary>
/// R0933 / TOR §10.1 — comparison between a new decision and the prior active
/// decision for the same (Solicitant, ServiceCode) pair. Drives the
/// "warn + allow refusal when new sum &lt; prior sum" branch of the
/// terminate-prior-on-acceptance lifecycle.
/// </summary>
/// <param name="HasPrior">
/// <c>true</c> when at least one prior active decision exists for the same
/// (Solicitant, ServiceCode) pair; <c>false</c> when the new decision is the
/// applicant's first.
/// </param>
/// <param name="PreviousDecisionSqid">
/// Sqid-encoded id of the prior active decision (when <see cref="HasPrior"/>
/// is true); null otherwise.
/// </param>
/// <param name="PriorAmount">
/// Prior decision's amount (MDL). Null when <see cref="HasPrior"/> is false or
/// the prior decision's form payload does not carry an amount.
/// </param>
/// <param name="NewAmount">
/// New decision's amount (MDL). Null when the new decision's form payload does
/// not carry an amount.
/// </param>
/// <param name="Difference">
/// Signed difference <c>NewAmount − PriorAmount</c> in MDL. Positive when the
/// new decision pays more, negative when it pays less, null when either side is
/// missing.
/// </param>
/// <param name="LowerSumWarning">
/// <c>true</c> when both amounts are known AND <see cref="NewAmount"/> &lt;
/// <see cref="PriorAmount"/>. The UI surfaces a confirmation prompt before the
/// decider may finalise — see R0933.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record DecisionComparisonDto(
    bool HasPrior,
    string? PreviousDecisionSqid,
    decimal? PriorAmount,
    decimal? NewAmount,
    decimal? Difference,
    bool LowerSumWarning);
