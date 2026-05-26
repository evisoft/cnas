namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0363 / TOR UC13 strategy 3 — record of one external-data refresh run. Each row
/// captures a single call to <c>IProfileRefreshService.RefreshFromSourceAsync</c> (one
/// contributor at a time) so operators can correlate the upstream pull with the
/// downstream child-table mutations it produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only.</b> Rows are inserted at run-start (with <see cref="Outcome"/> set
/// to a tentative value) and finalised at run-end. They are never updated after the
/// terminal <see cref="CompletedUtc"/> stamp lands so the audit trail is stable.
/// </para>
/// <para>
/// <b>Source-keyed index.</b> The EF configuration declares a composite index
/// <c>(Source, StartedUtc DESC)</c> for the common operator query "most recent runs
/// against RSP" without scanning the whole table.
/// </para>
/// <para>
/// <b>Failure summary cap.</b> <see cref="FailureSummary"/> is capped at 5000 chars to
/// keep the row's footprint bounded. If a partial-failure run exceeds the cap, the
/// service truncates the summary and appends an ellipsis — full per-row failures stay
/// in the structured logs and the audit-log row emitted alongside the refresh.
/// </para>
/// </remarks>
public sealed class ProfileRefreshRun : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable upstream-source code: <c>"RSP"</c>, <c>"RSUD"</c>, or <c>"SI_SFS"</c>.
    /// SCREAMING_SNAKE_CASE; case-sensitive. Unknown sources are rejected by the
    /// service before a row is written.
    /// </summary>
    public required string Source { get; set; }

    /// <summary>
    /// FK to the <see cref="InsuredPerson"/> whose profile was refreshed. Required for
    /// per-contributor runs; nullable to leave room for a future batch-run mode (one
    /// row per scheduled-job tick that touches many contributors).
    /// </summary>
    public long? TargetContributorId { get; set; }

    /// <summary>Final outcome classification. See <see cref="ProfileRefreshOutcome"/>.</summary>
    public ProfileRefreshOutcome Outcome { get; set; }

    /// <summary>Number of deltas successfully applied to the contributor's child tables.</summary>
    public int RowsApplied { get; set; }

    /// <summary>Number of deltas the service inspected but skipped (e.g. no-op writers, validator rejections).</summary>
    public int RowsSkipped { get; set; }

    /// <summary>UTC instant when the refresh call started.</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>
    /// UTC instant when the refresh call completed. Null while the row is still being
    /// populated (a crash mid-flight leaves the row pending forever — the operator can
    /// audit and decide whether to retry).
    /// </summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>
    /// Truncated free-form summary of per-delta failures when
    /// <see cref="Outcome"/> is <see cref="ProfileRefreshOutcome.PartialFailure"/> or
    /// <see cref="ProfileRefreshOutcome.Failed"/>. Capped at 5000 chars; the full list
    /// lives in the structured logs.
    /// </summary>
    public string? FailureSummary { get; set; }
}

/// <summary>
/// R0363 — outcome classification stamped on a finalised <see cref="ProfileRefreshRun"/>.
/// </summary>
public enum ProfileRefreshOutcome
{
    /// <summary>Every returned delta was applied successfully.</summary>
    Success = 0,

    /// <summary>The upstream returned no deltas — nothing to apply.</summary>
    NoChange = 1,

    /// <summary>Some deltas applied, some failed. <c>RowsApplied</c> + <c>RowsSkipped</c> show the split.</summary>
    PartialFailure = 2,

    /// <summary>The upstream call itself failed or no deltas applied. <c>RowsApplied</c> is 0.</summary>
    Failed = 3,
}
