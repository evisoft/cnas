namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Persisted definition of a single security-event notification rule (R0189 / SEC 048).
/// Each rule describes a pattern of interest on the <see cref="AuditLog"/> stream — when
/// the configured event-code pattern matches at least <see cref="ThresholdCount"/> rows
/// inside a rolling <see cref="WindowSeconds"/> window, the evaluator job
/// (<c>SecurityAlertEvaluatorJob</c>) fires an alert: queues an in-app notification per
/// recipient resolved from <see cref="RecipientGroup"/>, writes a
/// <c>SECURITY_ALERT.FIRED</c> audit row, and stamps <see cref="LastFiredAtUtc"/> so the
/// rule cannot re-fire for the next <see cref="CooldownSeconds"/> seconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why rules live in the DB.</b> R0189 surfaces curated, operator-meaningful alerts
/// to humans inside SI PS directly — distinct from R0190 which forwards the raw audit
/// stream to an external SIEM. Storing rules in Postgres (rather than appsettings.json)
/// lets the security team edit thresholds, add new patterns, and disable noisy rules
/// without redeploying. The admin UI surface that wraps these rows is deferred to
/// R0193's audit explorer / R0182's admin surface; until then operators edit via a
/// migration or a maintenance SQL script. The migration seeds four defaults that cover
/// the common SEC 048 cases.
/// </para>
/// <para>
/// <b>Pattern-matching seam.</b> <see cref="EventCodePattern"/> is a .NET regex applied
/// client-side to candidate rows. The evaluator job materialises the rolling window
/// once per fire (filtered by time in SQL) and runs the regex in process; the candidate
/// set is bounded by the time window and by an operator-configurable safety cap so the
/// in-memory pass is always cheap. Postgres-native <c>~</c> regex matching is NOT used
/// because the EF Core InMemory test provider does not implement it and we keep test +
/// production code on the same execution path (R0162 / R0164 precedent).
/// </para>
/// <para>
/// <b>Cooldown semantics.</b> <see cref="LastFiredAtUtc"/> + <see cref="CooldownSeconds"/>
/// defines a per-rule mute window. A rule that just fired will be skipped on every
/// subsequent evaluator iteration until the cooldown elapses, even if the underlying
/// audit pattern continues to satisfy the threshold. This is the back-pressure mechanism
/// that prevents alert storms — operators receive one signal per incident, not one per
/// matched row. Distinct from the in-channel-cardinality dedup that R0171 applies on
/// the notification side; cooldown is upstream of dispatch.
/// </para>
/// <para>
/// <b>Soft-delete pattern.</b> Inherits the standard <see cref="AuditableEntity.IsActive"/>
/// soft-delete flag. Operators MAY soft-delete a rule to disable it without losing the
/// historical <see cref="LastFiredAtUtc"/> stamp; the evaluator's query filters on
/// <c>IsActive == true</c>. Hard-delete is reserved for never-used seed rules an
/// operator explicitly purges.
/// </para>
/// <para>
/// <b>No <see cref="IExternalId"/>.</b> Rule rows are internal configuration; their
/// surrogate id never crosses the system boundary. The natural <see cref="Code"/> is the
/// public handle (e.g. <c>"FAILED_LOGIN_BURST"</c>) — stable across environments and
/// across migrations. If a future admin REST surface needs to expose rules over the
/// wire, the Sqid contract attaches at that point (per CLAUDE.md RULE 3) and the
/// marker is added in the same commit.
/// </para>
/// </remarks>
public sealed class SecurityAlertRule : AuditableEntity
{
    /// <summary>
    /// Stable, operator-facing rule identifier — the natural key. Used in audit details
    /// payloads (<c>ruleCode</c>), in counter tags (<c>rule.code</c>), and as the
    /// human-readable handle when operators discuss alerts in incident channels. Capped
    /// at 64 characters at the EF mapping layer and protected by a unique index so two
    /// rules can never share a code. The seed migration writes
    /// <c>"FAILED_LOGIN_BURST"</c>, <c>"ACCOUNT_LOCKED"</c>,
    /// <c>"REFRESH_REUSE_DETECTED"</c>, <c>"ADMIN_ELEVATION"</c>.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// .NET regex applied to <see cref="AuditLog.EventCode"/>. Anchored patterns are
    /// recommended (<c>^USER\.LOGIN\.FAIL$</c>); unanchored patterns silently match
    /// substrings. The evaluator caches compiled regexes process-wide (keyed by pattern
    /// string) and applies a 50 ms per-match timeout per CLAUDE.md regex-DoS guidance.
    /// Failures to compile a pattern log an error and skip the rule on every iteration
    /// — they do NOT crash the evaluator loop, so one malformed rule cannot wedge the
    /// whole subsystem.
    /// </summary>
    public required string EventCodePattern { get; set; }

    /// <summary>
    /// Rolling-window length in seconds. The evaluator counts matched rows whose
    /// <see cref="AuditLog.EventAtUtc"/> falls in <c>[now - WindowSeconds, now]</c>.
    /// Must be positive; the migration seeds windows of 60-300 s for the default
    /// rules. Configuration above ~one hour starts pushing the in-memory candidate
    /// scan past its useful bound — operators wanting longer windows should split the
    /// rule into multiple narrower windows or move the matching to SIEM (R0190).
    /// </summary>
    public int WindowSeconds { get; set; }

    /// <summary>
    /// Minimum matched-row count that triggers the rule. The evaluator fires when
    /// <c>matches &gt;= ThresholdCount</c> (NOT strictly greater than) so threshold 1 is
    /// the "any single occurrence" pattern (used by <c>REFRESH_REUSE_DETECTED</c> and
    /// <c>ACCOUNT_LOCKED</c>); threshold N is the "burst" pattern (used by
    /// <c>FAILED_LOGIN_BURST</c> with N=10).
    /// </summary>
    public int ThresholdCount { get; set; }

    /// <summary>
    /// Severity attached to the <c>SECURITY_ALERT.FIRED</c> audit row written when this
    /// rule fires. Drives downstream MLog mirroring (<see cref="AuditSeverity.Critical"/>
    /// events round-trip to MLog per SEC 056) and SIEM ingestion priority (R0190's
    /// <c>MinSeverity</c> filter). Defaults to <see cref="AuditSeverity.Notice"/>
    /// (R0189's "Warning" semantics); the <c>REFRESH_REUSE_DETECTED</c> seed rule bumps
    /// to <see cref="AuditSeverity.Sensitive"/> (R0189's "Error" semantics).
    /// </summary>
    public AuditSeverity AlertSeverity { get; set; } = AuditSeverity.Notice;

    /// <summary>
    /// Role code identifying the recipient group. The evaluator resolves the set of
    /// <see cref="UserProfile"/> rows where <see cref="UserProfile.Roles"/>
    /// contains this code and queues one in-app notification per match. Examples in
    /// the seed set: <c>"cnas-admin"</c> (failed-login burst, account locked) and
    /// <c>"cnas-tech-admin"</c> (refresh reuse, admin elevation). Capped at 64
    /// characters at the EF mapping layer to match the role-code convention used
    /// elsewhere.
    /// </summary>
    /// <remarks>
    /// If the recipient set is empty (no user carries the role), the rule still fires
    /// — the audit row, counter increment, and <see cref="LastFiredAtUtc"/> stamp all
    /// land — and the evaluator emits a WARN log so the operator can correct the
    /// role assignment. Surfacing the "rule fired but no recipients" diagnostic is
    /// preferable to silently swallowing the alert.
    /// </remarks>
    public required string RecipientGroup { get; set; }

    /// <summary>
    /// Per-rule mute window in seconds. After a rule fires, the evaluator skips it on
    /// every subsequent iteration until <c>LastFiredAtUtc + CooldownSeconds &lt;= now</c>.
    /// Defaults to 300 s (5 min) so an ongoing attack produces one notification per five
    /// minutes per rule rather than one per evaluator pass (the job runs every minute).
    /// Set to a small value (e.g. 60 s) for rules where every occurrence is
    /// independently actionable; set higher for noisy rules.
    /// </summary>
    public int CooldownSeconds { get; set; }

    /// <summary>
    /// UTC instant of the most recent successful fire. <c>null</c> until the rule has
    /// fired for the first time. The evaluator reads this column to enforce
    /// <see cref="CooldownSeconds"/> and writes it (alongside the audit row and the
    /// notification dispatches) atomically with the per-iteration
    /// <c>ICnasDbContext.SaveChangesAsync</c>. Operators may inspect it via the
    /// admin UI (R0193) to see which rules have ever fired in production.
    /// </summary>
    public DateTime? LastFiredAtUtc { get; set; }
}
