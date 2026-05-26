using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Recalculation;

/// <summary>
/// R1503 / TOR §3.7-D — value object returned by
/// <c>IBenefitRecalculationStrategy.RecomputeAsync</c>. Carries the projected
/// old / new amounts plus the per-decision status and a sanitised context
/// snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>No PII.</b> <see cref="Reason"/> and <see cref="RecalculationContextJson"/>
/// MUST NOT carry plaintext IDNP / IBAN / full names — the strategy is
/// responsible for redaction before returning the outcome. The orchestrator
/// persists the value verbatim.
/// </para>
/// <para>
/// <b>Status semantics.</b>
/// <list type="bullet">
///   <item><see cref="RecalculationResultStatus.Computed"/> — the new amount is valid; the row is eligible for Apply.</item>
///   <item><see cref="RecalculationResultStatus.Skipped"/> — the strategy declined to recompute (e.g. decision already cancelled). <see cref="Reason"/> required.</item>
///   <item><see cref="RecalculationResultStatus.Failed"/> — recoverable error during projection. <see cref="Reason"/> required.</item>
/// </list>
/// </para>
/// </remarks>
public sealed record BenefitRecalculationOutcome
{
    /// <summary>Per-decision outcome status. <see cref="RecalculationResultStatus.Applied"/> / <see cref="RecalculationResultStatus.Rejected"/> are NOT valid here — only the engine sets those.</summary>
    public required RecalculationResultStatus Status { get; init; }

    /// <summary>Amount payable under the OLD rules in MDL (pre-change projection).</summary>
    public required decimal OldAmountMdl { get; init; }

    /// <summary>Amount payable under the NEW rules in MDL (post-change projection).</summary>
    public required decimal NewAmountMdl { get; init; }

    /// <summary>HMAC IDNP hash of the beneficiary (base64, 44 chars). Empty when the decision has no beneficiary on file.</summary>
    public required string BeneficiaryIdnpHash { get; init; }

    /// <summary>Operator-facing reason; required when <see cref="Status"/> is Skipped / Failed.</summary>
    public string? Reason { get; init; }

    /// <summary>Strategy-supplied opaque JSON snapshot of the inputs that drove the projection (no PII).</summary>
    public string? RecalculationContextJson { get; init; }
}
