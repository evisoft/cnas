namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0116 + R0195 / TOR SEC 054-055 / CF 23.06-07 — admin-configurable MLog
/// dual-write toggle. Each row decides whether audit events tagged with the
/// matching <see cref="CategoryCode"/> are mirrored to the upstream MLog and
/// at what severity floor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Filtering pipeline.</b> The audit drainer consults this registry before
/// forwarding each row to MLog: a row is mirrored only when (a) a matching
/// <c>MLogCategoryConfig</c> exists with <see cref="IsEnabled"/> = <c>true</c>
/// AND (b) the row's <c>Severity</c> is at or above
/// <see cref="MinSeverity"/>. Categories that have NO matching row fall back
/// to "Critical-only" (the pre-R0195 default — only Critical events were
/// mirrored).
/// </para>
/// <para>
/// <b>Natural key.</b> <see cref="CategoryCode"/> is the stable identifier
/// (e.g. <c>AUTH</c>, <c>APPLICATION.RECEIVE</c>); it mirrors the codes
/// surfaced by <see cref="AuditCategory.Code"/>. Operators typically seed one
/// MLog config row per audit category but only the dual-write subset they
/// actually want to forward.
/// </para>
/// <para>
/// <b>External id.</b> Implements <see cref="IExternalId"/>; admins manage rows
/// by Sqid through <c>/api/admin/mlog/categories</c>.
/// </para>
/// </remarks>
public sealed class MLogCategoryConfig : AuditableEntity, IExternalId
{
    /// <summary>
    /// SCREAMING_SNAKE_CASE category code (e.g. <c>AUTH</c>,
    /// <c>APPLICATION.RECEIVE</c>) matching the prefix of the audit
    /// <c>EventCode</c> the operator wants the toggle to govern. Pattern
    /// <c>^[A-Z][A-Z0-9_.]{1,63}$</c>. Length ≤ 64. Unique.
    /// </summary>
    public string CategoryCode { get; set; } = string.Empty;

    /// <summary>Human-readable display name. Bounded to 256 characters.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Dual-write toggle. When <c>false</c> the matching events are NOT mirrored
    /// to MLog regardless of severity. Default <c>true</c> so a newly-inserted
    /// row starts in the "mirror everything at or above MinSeverity" position.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Severity floor for MLog mirroring. Events below this level are filtered
    /// out even when <see cref="IsEnabled"/> is <c>true</c>. Default
    /// <see cref="MLogSeverityFloor.Notice"/> so the registry behaves as a
    /// "notice-and-above" mirror unless tuned tighter or wider.
    /// </summary>
    public MLogSeverityFloor MinSeverity { get; set; } = MLogSeverityFloor.Notice;

    /// <summary>
    /// Internal user id of the operator that last mutated this row. Paired with
    /// <see cref="AuditableEntity.UpdatedBy"/> for audit attribution.
    /// </summary>
    public long? UpdatedByUserId { get; set; }

    /// <summary>Maximum length of <see cref="CategoryCode"/> in characters.</summary>
    public const int MaxCategoryCodeLength = 64;

    /// <summary>Maximum length of <see cref="DisplayName"/> in characters.</summary>
    public const int MaxDisplayNameLength = 256;
}

/// <summary>
/// R0116 + R0195 / TOR SEC 054-055 — coarse severity-floor enum used by the
/// MLog dual-write filter. Distinct from the full <see cref="AuditSeverity"/>
/// enum so operators don't have to surface every severity level on the admin
/// UI — the registry filter only cares about two thresholds: "notice and
/// above" or "critical only".
/// </summary>
public enum MLogSeverityFloor
{
    /// <summary>
    /// Mirror events at <see cref="AuditSeverity.Notice"/> severity or higher
    /// (Notice / Sensitive / Critical). Information events are suppressed.
    /// </summary>
    Notice = 0,

    /// <summary>
    /// Mirror only <see cref="AuditSeverity.Critical"/> events. Matches the
    /// pre-R0195 default behaviour and is the safe-by-default position for
    /// categories the operator wants kept on the local-only journal.
    /// </summary>
    Critical = 1,
}
