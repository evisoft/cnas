using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R1505 / TOR §3.7-F — lifecycle states of a recovery (recuperare a sumelor)
/// decision issued by CNAS against a beneficiary who has received funds the
/// system later determines were not owed.
/// </summary>
/// <remarks>
/// Stable enum-name strings cross the wire (the DTO serialises by name). Append
/// new values at the end of the enum to keep the wire contract additive — the
/// integer ordinals are persisted on the underlying <c>Document.Verdict</c>
/// column so renumbering would break historical rows.
/// </remarks>
public enum RecoveryDecisionStatus
{
    /// <summary>Recovery decision has been issued and is awaiting solicitant acknowledgement.</summary>
    Initiated = 0,

    /// <summary>Solicitant has acknowledged the debt (signed, accepted notification, etc.).</summary>
    Acknowledged = 1,

    /// <summary>Partial recovery has been collected against the underlying MPay reversal.</summary>
    PartiallyRecovered = 2,

    /// <summary>Full debt has been recovered; the case is closed.</summary>
    FullyRecovered = 3,
}

/// <summary>
/// R1505 / TOR §3.7-F — input envelope for initiating a recovery decision.
/// </summary>
/// <param name="SolicitantSqid">Sqid-encoded id of the beneficiary the recovery targets. Required.</param>
/// <param name="Amount">Amount in MDL to recover. Must be greater than zero.</param>
/// <param name="Reason">
/// Free-text justification (3-500 chars) recorded against the decision for audit
/// traceability. Surfaced verbatim on the rendered DOCX template (Annex 7).
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record RecoveryDecisionInputDto(
    string SolicitantSqid,
    decimal Amount,
    string Reason);

/// <summary>
/// R1505 / TOR §3.7-F — outcome of a recovery-workflow state transition. All
/// identifiers are Sqid-encoded per CLAUDE.md RULE 3.
/// </summary>
/// <param name="Sqid">Sqid-encoded id of the recovery <c>Document</c> row.</param>
/// <param name="SolicitantSqid">Sqid-encoded id of the targeted beneficiary.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="AmountMdl">Original recovery amount in MDL.</param>
/// <param name="RecoveredAmountMdl">
/// Total amount already recovered against the decision (sums every prior
/// <c>MarkRecoveredAsync</c> call). Equals <paramref name="AmountMdl"/> when the
/// decision reaches <see cref="RecoveryDecisionStatus.FullyRecovered"/>.
/// </param>
/// <param name="Reason">Justification recorded at initiation.</param>
/// <param name="InitiatedAtUtc">UTC instant the decision was issued.</param>
/// <param name="AcknowledgedAtUtc">UTC instant the solicitant acknowledged the debt, when applicable.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record RecoveryDecisionDto(
    [property: SensitivityClassification(SensitivityLabel.Public)] string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Public)] string SolicitantSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)] RecoveryDecisionStatus Status,
    [property: SensitivityClassification(SensitivityLabel.Internal)] decimal AmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)] decimal RecoveredAmountMdl,
    [property: SensitivityClassification(SensitivityLabel.Internal)] string Reason,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime InitiatedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime? AcknowledgedAtUtc);

/// <summary>
/// R1505 — request body for the <c>POST /api/decisions/recovery/{sqid}/recovered</c>
/// endpoint. Records a recovered-amount payment against a prior recovery decision.
/// </summary>
/// <param name="RecoveredAmount">
/// Amount recovered this round in MDL. Must be strictly positive; the FluentValidation
/// rule at the Application boundary additionally caps the value to a generous typo
/// guard ceiling.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record RecoveryRecordedInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)] decimal RecoveredAmount);
