using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0942 / TOR §10.1 — outcome of an automatic
/// <c>IRefusedPensionFallbackCascade.EvaluateAsync</c> invocation. Carries the
/// result the controller / state-machine surface needs to know what happened,
/// without exposing internal primary keys.
/// </summary>
/// <param name="WasCascadeTriggered">
/// <c>true</c> when the cascade detected a refused pension AND created a follow-up
/// AlocatieSociala draft; <c>false</c> on any no-op branch.
/// </param>
/// <param name="ReasonCode">
/// Stable machine-readable reason code; one of:
/// <c>"FALLBACK_INITIATED"</c>, <c>"NOT_A_PENSION_REFUSAL"</c>,
/// <c>"FEATURE_DISABLED"</c>, <c>"ALREADY_CASCADED"</c>,
/// <c>"TARGET_PASSPORT_MISSING"</c>.
/// </param>
/// <param name="FallbackApplicationSqid">
/// Sqid-encoded id of the newly-created follow-up
/// <c>ServiceApplication</c> draft. Non-null only when
/// <see cref="WasCascadeTriggered"/> is true.
/// </param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record FallbackCascadeOutcomeDto(
    bool WasCascadeTriggered,
    string ReasonCode,
    string? FallbackApplicationSqid);
