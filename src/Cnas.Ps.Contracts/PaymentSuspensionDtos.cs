using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R1504 / TOR §3.7-E — input envelope for suspend / resume payment ceremonies.
/// </summary>
/// <param name="Reason">
/// Free-text justification (3-500 chars) recorded against the lifecycle row
/// and surfaced verbatim on the rendered DOCX (Decizie / Dispozitie).
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record PaymentSuspensionInputDto(string Reason);

/// <summary>
/// R1504 / TOR §3.7-E — projection of a payment-suspension lifecycle row.
/// All ids are Sqid-encoded per CLAUDE.md RULE 3.
/// </summary>
/// <param name="Sqid">Sqid-encoded id of the suspension record.</param>
/// <param name="DecisionSqid">Sqid-encoded id of the prior decision the suspension targets.</param>
/// <param name="SuspensionReason">Reason recorded at suspend time.</param>
/// <param name="SuspendedAtUtc">UTC instant the suspension was issued.</param>
/// <param name="ResumedAtUtc">UTC instant payments were resumed; <c>null</c> while still suspended.</param>
/// <param name="ResumeReason">Reason recorded at resume time; <c>null</c> while still suspended.</param>
/// <param name="SuspensionDocumentSqid">Sqid of the rendered Decizie document; nullable.</param>
/// <param name="ResumeDocumentSqid">Sqid of the rendered Dispozitie document; nullable.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record PaymentSuspensionDto(
    [property: SensitivityClassification(SensitivityLabel.Public)] string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Public)] string DecisionSqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)] string SuspensionReason,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime SuspendedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)] DateTime? ResumedAtUtc,
    [property: SensitivityClassification(SensitivityLabel.Internal)] string? ResumeReason,
    [property: SensitivityClassification(SensitivityLabel.Public)] string? SuspensionDocumentSqid,
    [property: SensitivityClassification(SensitivityLabel.Public)] string? ResumeDocumentSqid);
