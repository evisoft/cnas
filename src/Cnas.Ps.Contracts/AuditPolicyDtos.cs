namespace Cnas.Ps.Contracts;

/// <summary>
/// R0182 / SEC 042 — single row in the admin-configurable audit-policy registry. All
/// id fields are Sqid-encoded strings per CLAUDE.md RULE 3; the natural-key
/// <c>Code</c> remains a raw stable string because operators reference policies
/// by code in runbooks and admin URLs.
/// </summary>
/// <param name="Id">Sqid-encoded id of the policy row.</param>
/// <param name="Code">Stable kebab-case identifier (e.g. <c>solicitant.view.search</c>).</param>
/// <param name="Module">Coarse module grouping (e.g. <c>Solicitant</c>).</param>
/// <param name="Screen">Finer screen identifier (e.g. <c>Search</c>).</param>
/// <param name="DataCategory">Optional data-category tag (e.g. <c>PII</c>); null when unspecified.</param>
/// <param name="EventCodePattern">.NET regex matched against the audit row's event code.</param>
/// <param name="OverrideSeverity">
/// Stable string form of the <c>AuditSeverity</c> enum (<c>Information</c> | <c>Notice</c>
/// | <c>Sensitive</c> | <c>Critical</c>) — null preserves the caller-supplied severity. The
/// string form is used so this assembly stays Core-free per the Contracts project's
/// zero-dependency invariant.
/// </param>
/// <param name="SuppressAudit">
/// When <c>true</c>, matched rows are dropped instead of persisted. Permitted only
/// when the effective severity resolves to <c>Information</c>.
/// </param>
/// <param name="ExtraRedactKeys">
/// Additional JSON keys merged into the PII redactor's default substring set on
/// matched rows. Empty list means no extra redaction beyond the defaults.
/// </param>
/// <param name="Priority">
/// Resolution priority — lower wins, ties broken by id ascending. Default 100.
/// </param>
/// <param name="IsEnabled">Enable flag distinct from soft-delete.</param>
/// <param name="Description">Free-form admin-facing description; nullable.</param>
public sealed record AuditPolicyOutput(
    string Id,
    string Code,
    string Module,
    string Screen,
    string? DataCategory,
    string EventCodePattern,
    string? OverrideSeverity,
    bool SuppressAudit,
    IReadOnlyList<string> ExtraRedactKeys,
    int Priority,
    bool IsEnabled,
    string? Description);

/// <summary>
/// Request body for <c>POST /api/audit-policies</c>. The audit-policy CRUD surface is
/// restricted to the tech-admin role; mass-assignment protection (per CLAUDE.md §2.4)
/// is enforced by the controller's <c>[Authorize]</c> policy + the absence of any
/// audit / system fields on this input.
/// </summary>
/// <param name="Code">Stable kebab-case identifier; validator regex <c>^[a-z][a-z0-9.-]{2,79}$</c>.</param>
/// <param name="Module">Coarse module grouping; required.</param>
/// <param name="Screen">Finer screen identifier; required.</param>
/// <param name="DataCategory">Optional data-category tag.</param>
/// <param name="EventCodePattern">.NET regex; validator verifies the pattern compiles.</param>
/// <param name="OverrideSeverity">
/// Optional severity override as a stable string (<c>Information</c> | <c>Notice</c> |
/// <c>Sensitive</c> | <c>Critical</c>); null preserves the caller-supplied severity.
/// </param>
/// <param name="SuppressAudit">Suppression flag; must be paired with Information severity.</param>
/// <param name="ExtraRedactKeys">Additional JSON redact keys (case-insensitive substring match).</param>
/// <param name="Priority">Resolution priority; must be &gt;= 0. Default 100.</param>
/// <param name="IsEnabled">Enable flag; defaults to true.</param>
/// <param name="Description">Optional admin-facing description.</param>
public sealed record AuditPolicyCreateInput(
    string Code,
    string Module,
    string Screen,
    string? DataCategory,
    string EventCodePattern,
    string? OverrideSeverity,
    bool SuppressAudit,
    IReadOnlyList<string> ExtraRedactKeys,
    int Priority = 100,
    bool IsEnabled = true,
    string? Description = null);

/// <summary>
/// Request body for <c>PUT /api/audit-policies/{sqid}</c>. <c>Code</c> is intentionally
/// immutable after create — operator runbooks reference policies by code, and
/// re-keying a row would break those references. Mutate by disabling the old policy
/// and creating a new one instead.
/// </summary>
/// <param name="Module">Updated coarse module grouping.</param>
/// <param name="Screen">Updated finer screen identifier.</param>
/// <param name="DataCategory">Updated optional data-category tag.</param>
/// <param name="EventCodePattern">Updated .NET regex; validator verifies the pattern compiles.</param>
/// <param name="OverrideSeverity">
/// Updated severity override (nullable). Stable string form as on
/// <see cref="AuditPolicyCreateInput.OverrideSeverity"/>.
/// </param>
/// <param name="SuppressAudit">Updated suppression flag.</param>
/// <param name="ExtraRedactKeys">Updated redact-key set.</param>
/// <param name="Priority">Updated resolution priority.</param>
/// <param name="IsEnabled">Updated enable flag.</param>
/// <param name="Description">Updated description.</param>
public sealed record AuditPolicyUpdateInput(
    string Module,
    string Screen,
    string? DataCategory,
    string EventCodePattern,
    string? OverrideSeverity,
    bool SuppressAudit,
    IReadOnlyList<string> ExtraRedactKeys,
    int Priority,
    bool IsEnabled,
    string? Description);
