namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R1503 / TOR §3.7-D — one execution of the mass-recalculation engine
/// against a <see cref="LegalChangeEvent"/>. The orchestrator creates a
/// <see cref="RecalculationRunStatus.Running"/> row at the top of every
/// dispatch and finalises it as <see cref="RecalculationRunStatus.Completed"/>
/// or <see cref="RecalculationRunStatus.Failed"/> at the bottom.
/// </summary>
/// <remarks>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/> because the
/// outbound DTO (<c>Cnas.Ps.Contracts.RecalculationRunDto.Id</c>) carries a
/// Sqid-encoded surrogate per CLAUDE.md RULE 3.
/// </para>
/// <para>
/// <b>Aggregated counters only.</b> The run row carries totals; per-decision
/// detail lives on <see cref="RecalculationDecisionResult"/> rows pointed by
/// the <see cref="LegalChangeEventId"/> + run id pair.
/// </para>
/// </remarks>
public sealed class RecalculationRun : AuditableEntity, IExternalId
{
    /// <summary>FK to the <see cref="LegalChangeEvent"/> the run was triggered against.</summary>
    public long LegalChangeEventId { get; set; }

    /// <summary>Whether the run was fired by the scheduler or a manual operator action.</summary>
    public RecalculationTriggerKind TriggerKind { get; set; }

    /// <summary>DryRun (no benefit-decision mutations) or Apply (writes via strategy.ApplyAsync).</summary>
    public RecalculationMode Mode { get; set; }

    /// <summary>Lifecycle status — defaults to <see cref="RecalculationRunStatus.Running"/>.</summary>
    public RecalculationRunStatus Status { get; set; } = RecalculationRunStatus.Running;

    /// <summary>UTC timestamp the orchestrator started the run.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC timestamp the run completed; null while still <see cref="RecalculationRunStatus.Running"/>.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Total decisions scanned across every strategy invocation.</summary>
    public long TotalDecisionsScanned { get; set; }

    /// <summary>Total decisions for which the strategy produced a Computed row.</summary>
    public long TotalDecisionsRecalculated { get; set; }

    /// <summary>Total decisions tagged Skipped (no strategy registered, or strategy declined).</summary>
    public long TotalSkipped { get; set; }

    /// <summary>Total decisions tagged Failed (strategy threw).</summary>
    public long TotalFailed { get; set; }

    /// <summary>
    /// Net delta across every result row in MDL: <c>Σ(NewAmountMdl - OldAmountMdl)</c>.
    /// Positive when the change raises benefits; negative when it lowers them.
    /// </summary>
    public decimal TotalDeltaMdl { get; set; }

    /// <summary>Operator-facing reason populated when <see cref="Status"/> is <see cref="RecalculationRunStatus.Failed"/>.</summary>
    public string? FailureReason { get; set; }
}
