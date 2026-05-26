using System.Collections.Generic;

namespace Cnas.Ps.Core.Domain;

/// <summary>
/// R0182 / SEC 042 — admin-configurable audit policy that the audit write pipeline
/// consults at flush time to override (or suppress) caller-supplied severity / redaction
/// on a per-module / per-screen / per-data-category basis.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this entity exists.</b> Before R0182 every <see cref="AuditLog"/> row carried the
/// severity the producer hard-coded at the call site. That worked for the baseline
/// regulatory cases but left site administrators without a knob to silence noisy event
/// codes, escalate sensitive-data reads, or extend the PII redaction map without
/// redeploying. SEC 042 mandates that audit categories be administrable; R0182 wires
/// the persisted policy set into the drainer + replay write paths so changes take
/// effect within one cache refresh tick (60 s by default) without touching code.
/// </para>
/// <para>
/// <b>Resolution algorithm.</b> Per audit-event write the resolver iterates the active
/// policy set, filters by <see cref="Module"/> / <see cref="Screen"/> / <see cref="DataCategory"/>
/// where the caller supplied a value (null filters bypass), then matches
/// <see cref="EventCodePattern"/> against the row's
/// <see cref="AuditLog.EventCode"/>. Policies are sorted ascending by
/// <see cref="Priority"/> with the surrogate <see cref="AuditableEntity.Id"/> as the
/// tie-breaker. The first match wins and its <see cref="OverrideSeverity"/> /
/// <see cref="SuppressAudit"/> / <see cref="ExtraRedactKeys"/> apply to the row.
/// </para>
/// <para>
/// <b>Safeguard.</b> <see cref="SuppressAudit"/> is permitted ONLY for
/// <see cref="AuditSeverity.Information"/>. The validator rejects suppression policies
/// paired with <c>Notice</c> / <c>Sensitive</c> / <c>Critical</c> overrides at write
/// time; defense-in-depth at the drainer also refuses to drop a row whose effective
/// severity is anything other than Information (logged + counted as
/// <c>cnas.audit.policy_misconfig</c>). Critical events MUST land in the journal
/// regardless of any operator misconfiguration.
/// </para>
/// <para>
/// <b>Soft delete = disabled.</b> Inherits the standard <see cref="AuditableEntity.IsActive"/>
/// flag. Operators disable a policy by setting that flag to false; the resolver's
/// <see cref="IsEnabled"/> AND <c>IsActive</c> predicate skips inactive rows. The CRUD
/// service's <c>DisableAsync</c> is the public knob — it flips both flags and emits
/// the Critical mutation audit row.
/// </para>
/// <para>
/// <b>Regex DoS guard.</b> Mirrors the R0189 evaluator's pattern cache: regexes are
/// compiled with <see cref="System.Text.RegularExpressions.RegexOptions.Compiled"/> +
/// <see cref="System.Text.RegularExpressions.RegexOptions.CultureInvariant"/> and a
/// 50 ms per-match timeout. Invalid patterns are caught at validation time; if a
/// previously-valid pattern starts misbehaving at runtime the resolver swallows the
/// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/> and treats
/// the policy as a no-match so a single bad row cannot wedge the audit pipeline.
/// </para>
/// <para>
/// <b>External id contract.</b> Implements <see cref="IExternalId"/> because the CRUD
/// service exposes policies over an admin REST surface and consumers reference rows
/// by their Sqid-encoded id — see <c>IAuditPolicyService</c> / <c>AuditPoliciesController</c>.
/// The natural <see cref="Code"/> remains the stable cross-environment handle used in
/// the admin UI URL paths; the Sqid is the wire-level identifier.
/// </para>
/// </remarks>
public sealed class AuditPolicy : AuditableEntity, IExternalId
{
    /// <summary>
    /// Stable, operator-facing identifier — the natural key. Kebab-case, lower-case ASCII
    /// (regex <c>^[a-z][a-z0-9.-]{2,79}$</c>). Examples seeded by the migration:
    /// <c>solicitant.view.search</c>, <c>cerere.bulk.export</c>,
    /// <c>usermgmt.role.change</c>. Capped at 80 characters at the EF mapping layer and
    /// protected by a unique index. Stable across environments and across migrations so
    /// operator runbooks can reference policies by code.
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// Coarse-grained module grouping for filter-by-module resolution. Examples:
    /// <c>Solicitant</c>, <c>Cerere</c>, <c>Decision</c>, <c>UserMgmt</c>. Capped at 64
    /// characters. When the caller passes <c>module = null</c> at resolve time, this
    /// filter is bypassed (the policy is considered a candidate regardless of its
    /// module).
    /// </summary>
    public required string Module { get; set; }

    /// <summary>
    /// Finer-grained screen identifier within the <see cref="Module"/>. Examples:
    /// <c>Search</c>, <c>Detail</c>, <c>Edit</c>, <c>Export</c>. Capped at 64 characters.
    /// Null-filter bypass mirrors <see cref="Module"/>.
    /// </summary>
    public required string Screen { get; set; }

    /// <summary>
    /// Optional data-category tag. Examples: <c>PII</c>, <c>Financial</c>, <c>Decision</c>,
    /// <c>Document</c>. Capped at 32 characters. Null when the policy applies regardless
    /// of category; non-null when the operator wants the policy to apply only to events
    /// that carry the matching category. Null-filter bypass at resolve time mirrors
    /// <see cref="Module"/> / <see cref="Screen"/>.
    /// </summary>
    public string? DataCategory { get; set; }

    /// <summary>
    /// .NET regex applied to <see cref="AuditLog.EventCode"/> on candidate rows.
    /// Anchored patterns are recommended (<c>^SOLICITANT\.VIEW\.SEARCH$</c>); unanchored
    /// patterns silently match substrings. Capped at 256 characters at the EF mapping
    /// layer. Compiled once by the resolver with <see cref="System.Text.RegularExpressions.RegexOptions.Compiled"/>
    /// + <see cref="System.Text.RegularExpressions.RegexOptions.CultureInvariant"/> and a
    /// 50 ms per-match timeout (mirrors R0189).
    /// </summary>
    public required string EventCodePattern { get; set; }

    /// <summary>
    /// When non-null, replaces the caller-supplied severity on the matched row.
    /// <c>null</c> means "leave the caller's severity untouched" — useful when the
    /// policy only contributes <see cref="ExtraRedactKeys"/> or
    /// <see cref="SuppressAudit"/> without changing severity.
    /// </summary>
    public AuditSeverity? OverrideSeverity { get; set; }

    /// <summary>
    /// When <c>true</c>, the resolver instructs the drainer to drop the matched row
    /// rather than persist it. Permitted ONLY for events whose effective severity
    /// resolves to <see cref="AuditSeverity.Information"/>; the validator rejects
    /// suppression paired with <see cref="OverrideSeverity"/> = Notice / Sensitive /
    /// Critical at write-time. The drainer additionally refuses to suppress non-
    /// Information rows at flush time (defense-in-depth — see class remarks).
    /// </summary>
    public bool SuppressAudit { get; set; }

    /// <summary>
    /// Additional JSON keys merged into <c>PiiRedactor</c>'s default substring set
    /// when the policy matches. Example: <c>["iban", "bankAccount"]</c> for a
    /// solicitant-detail policy that surfaces financial data. Empty list means the
    /// policy adds no extra redaction; the default redactor list still applies.
    /// </summary>
    public List<string> ExtraRedactKeys { get; set; } = new();

    /// <summary>
    /// Resolution priority — LOWER wins. The resolver sorts candidates ascending by
    /// <see cref="Priority"/> then by <see cref="AuditableEntity.Id"/> ascending and
    /// returns the first match. Default 100 leaves headroom (50, 200, etc.) for
    /// operator overrides that should take precedence over (or yield to) the seeded
    /// defaults without renumbering every row. Must be &gt;= 0; the validator
    /// enforces.
    /// </summary>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Enable flag distinct from <see cref="AuditableEntity.IsActive"/>. Both are
    /// AND-ed at resolve time so an operator can soft-disable a policy without
    /// soft-deleting the row, leaving CRUD history intact for the audit explorer
    /// (R0193).
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Free-form admin-facing description of why the policy exists. Surfaces in the
    /// admin UI (R0193) and in the audit trail of mutations. Capped at 512 characters
    /// at the EF mapping layer.
    /// </summary>
    public string? Description { get; set; }
}
