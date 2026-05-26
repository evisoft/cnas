using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0182 / SEC 042 — synchronous, hot-path lookup that the audit drainer consults
/// before persisting an <see cref="AuditEventRecord"/>. Returns the effective
/// severity, suppression flag, extra redact keys, and the matched policy code so
/// the drainer can apply the admin-configured overrides on every row without
/// blocking on a database round-trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hot-path discipline.</b> Implementations MUST be non-blocking — the drainer
/// invokes <see cref="Resolve"/> once per buffered <see cref="AuditEventRecord"/>
/// inside the per-batch flush. The reference implementation
/// (<c>AuditPolicyResolver</c>) backs this by an in-memory snapshot of the policy
/// table that a background refresh job rebuilds on a 60 s cadence (or on demand
/// when CRUD mutates a row).
/// </para>
/// <para>
/// <b>No-match contract.</b> When no enabled policy matches the supplied
/// event code + filters, the resolver returns
/// <see cref="ResolvedAuditPolicy.PassThrough"/> — the caller's severity is
/// preserved, suppression is false, and the extra-redact set is empty. Callers
/// never have to branch on "policy missing" vs. "policy explicitly says no-op".
/// </para>
/// <para>
/// <b>Result vs exception.</b> The method returns <see cref="Result{T}"/> rather
/// than throwing because the audit pipeline must NEVER throw on a misconfigured
/// policy — a malformed regex or an unexpected lookup failure must surface as a
/// failure result that the drainer can log and route to the pass-through default
/// rather than abort the flush. The reference implementation returns failure only
/// on internal errors; the default no-match path is a success carrying the
/// pass-through projection.
/// </para>
/// </remarks>
public interface IAuditPolicyResolver
{
    /// <summary>
    /// Resolves the effective audit policy for a single audit-event write.
    /// </summary>
    /// <param name="eventCode">
    /// Stable event code (e.g. <c>SOLICITANT.VIEW.SEARCH</c>) — matched against
    /// every active policy's <see cref="AuditPolicy.EventCodePattern"/>.
    /// </param>
    /// <param name="callerSeverity">
    /// Severity the producer passed to <c>IAuditService.RecordAsync</c>. Used as
    /// the effective severity when no policy matches or when the matched policy's
    /// <see cref="AuditPolicy.OverrideSeverity"/> is <c>null</c>.
    /// </param>
    /// <param name="module">
    /// Optional coarse module filter (e.g. <c>Solicitant</c>). <c>null</c>
    /// bypasses the module filter — every policy regardless of its <c>Module</c>
    /// is a candidate.
    /// </param>
    /// <param name="screen">
    /// Optional finer screen filter (e.g. <c>Search</c>). Null-filter bypass
    /// mirrors <paramref name="module"/>.
    /// </param>
    /// <param name="dataCategory">
    /// Optional data-category filter (e.g. <c>PII</c>). Null-filter bypass
    /// mirrors <paramref name="module"/>.
    /// </param>
    /// <returns>
    /// On success the resolved projection — either a pass-through (no match) or
    /// the lowest-priority matching policy's effective shape. Failure results
    /// indicate a resolver-internal problem and instruct the caller to fall back
    /// to pass-through behaviour.
    /// </returns>
    Result<ResolvedAuditPolicy> Resolve(
        string eventCode,
        AuditSeverity callerSeverity,
        string? module = null,
        string? screen = null,
        string? dataCategory = null);
}

/// <summary>
/// Output projection of <see cref="IAuditPolicyResolver.Resolve"/>. Carries the
/// minimum information the drainer needs to apply an admin-configured audit
/// override on a single in-flight record.
/// </summary>
/// <param name="EffectiveSeverity">
/// Severity the drainer will stamp on the persisted <see cref="AuditLog"/> row.
/// Equals the caller-supplied severity when no policy matched or when the matched
/// policy did not override it.
/// </param>
/// <param name="Suppress">
/// When <c>true</c>, the drainer drops the row instead of persisting. Per R0182
/// suppression is permitted ONLY for events whose effective severity resolves to
/// <see cref="AuditSeverity.Information"/>; the drainer enforces the safeguard at
/// flush time as defense-in-depth.
/// </param>
/// <param name="ExtraRedactKeys">
/// Additional JSON keys merged into <c>PiiRedactor</c>'s default substring set
/// when redacting this row's <c>DetailsJson</c>. The drainer applies the merged
/// set BEFORE computing the SHA-256 row hash so chain integrity reflects the
/// final on-disk payload.
/// </param>
/// <param name="MatchedPolicyCode">
/// <see cref="AuditPolicy.Code"/> of the policy that produced this projection, or
/// <c>null</c> when no policy matched. Useful for diagnostic logging + the
/// per-policy <c>cnas.audit.policy_suppressed</c> counter tag.
/// </param>
public sealed record ResolvedAuditPolicy(
    AuditSeverity EffectiveSeverity,
    bool Suppress,
    IReadOnlySet<string> ExtraRedactKeys,
    string? MatchedPolicyCode)
{
    /// <summary>Empty set instance reused by the pass-through projection.</summary>
    private static readonly IReadOnlySet<string> EmptyRedactKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the "no policy matched" projection for the supplied caller severity.
    /// The drainer falls back to this whenever <see cref="IAuditPolicyResolver.Resolve"/>
    /// finds zero candidate policies or fails internally.
    /// </summary>
    /// <param name="callerSeverity">Severity passed by the audit-event producer.</param>
    /// <returns>A pass-through projection that preserves the caller's severity.</returns>
    public static ResolvedAuditPolicy PassThrough(AuditSeverity callerSeverity) =>
        new(callerSeverity, Suppress: false, EmptyRedactKeys, MatchedPolicyCode: null);
}
