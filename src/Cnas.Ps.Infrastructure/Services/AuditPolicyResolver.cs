using System.Text.RegularExpressions;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Reference implementation of <see cref="IAuditPolicyResolver"/>. Maintains an
/// in-memory snapshot of the <see cref="AuditPolicy"/> table that the audit drainer
/// consults on every record write without paying for a DB round-trip. The snapshot
/// is rebuilt by the <c>AuditPolicyCacheRefreshJob</c> background service on a 60 s
/// cadence by default; the CRUD service additionally triggers a synchronous refresh
/// via <see cref="InvalidateAsync"/> after every mutation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot atomicity.</b> The snapshot is held as a single
/// <see cref="CompiledAuditPolicy"/> array reference; <see cref="Resolve"/> reads it
/// once per call so a refresh that completes mid-resolve never produces a partially
/// updated view. Writes (refresh) build a fresh array off-line and swap the
/// reference atomically with <see cref="System.Threading.Interlocked.Exchange{T}(ref T, T)"/>.
/// </para>
/// <para>
/// <b>Regex DoS guard.</b> Patterns are compiled once with
/// <see cref="RegexOptions.Compiled"/> + <see cref="RegexOptions.CultureInvariant"/>
/// and a 50 ms per-match timeout (mirrors R0189). Invalid patterns observed during
/// snapshot rebuild are logged at WARN and dropped from the snapshot — the resolver
/// continues serving the rest of the rule set so one bad row cannot wedge the
/// audit pipeline.
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton because the cache state must outlive
/// any single scope. The refresh job resolves the singleton via the DI scope factory.
/// </para>
/// </remarks>
public sealed class AuditPolicyResolver : IAuditPolicyResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AuditPolicyResolver> _logger;

    /// <summary>
    /// Current snapshot. Replaced atomically by <see cref="InvalidateAsync"/> and the
    /// background refresh job; read directly by <see cref="Resolve"/>. Starts as an
    /// empty array so the resolver is safe to query before the first refresh
    /// completes — the pass-through fallback handles the no-match case.
    /// </summary>
    private CompiledAuditPolicy[] _snapshot = Array.Empty<CompiledAuditPolicy>();

    /// <summary>Constructs the resolver with its DI scope factory + logger.</summary>
    /// <param name="scopes">Scope factory used to materialise <see cref="IReadOnlyCnasDbContext"/> per refresh.</param>
    /// <param name="logger">Structured logger for invalid-pattern + refresh diagnostics.</param>
    public AuditPolicyResolver(IServiceScopeFactory scopes, ILogger<AuditPolicyResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public Result<ResolvedAuditPolicy> Resolve(
        string eventCode,
        AuditSeverity callerSeverity,
        string? module = null,
        string? screen = null,
        string? dataCategory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventCode);

        // Take a stable reference to the current snapshot. A concurrent refresh may
        // swap the field after we read it — we still see a consistent view of the
        // policies that were live at the moment of resolution.
        var current = _snapshot;
        if (current.Length == 0)
        {
            return Result<ResolvedAuditPolicy>.Success(ResolvedAuditPolicy.PassThrough(callerSeverity));
        }

        // Iterate ordered candidates (already sorted by Priority ASC, Id ASC at
        // snapshot-build time) and return the first hit.
        foreach (var p in current)
        {
            if (!FilterMatches(p.Module, module)) continue;
            if (!FilterMatches(p.Screen, screen)) continue;
            if (!FilterMatches(p.DataCategory, dataCategory)) continue;
            if (!SafeIsMatch(p.Regex, eventCode)) continue;

            // First match wins. Compute the effective severity: an explicit override
            // replaces the caller's severity; otherwise the caller's value passes
            // through. Suppression is reported verbatim; the drainer enforces the
            // "non-Information cannot be suppressed" safeguard separately.
            var effective = p.OverrideSeverity ?? callerSeverity;
            return Result<ResolvedAuditPolicy>.Success(new ResolvedAuditPolicy(
                EffectiveSeverity: effective,
                Suppress: p.SuppressAudit,
                ExtraRedactKeys: p.ExtraRedactKeys,
                MatchedPolicyCode: p.Code));
        }

        return Result<ResolvedAuditPolicy>.Success(ResolvedAuditPolicy.PassThrough(callerSeverity));
    }

    /// <summary>
    /// Rebuilds the in-memory snapshot from the latest persisted state. Invoked by
    /// the background refresh job on its cadence and synchronously by
    /// <see cref="IAuditPolicyService"/> after every successful mutation so the
    /// caller's change is visible to the next audit-event write without waiting for
    /// the next refresh tick.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task that completes when the swap has happened.</returns>
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();

        var rows = await db.AuditPolicies
            .Where(p => p.IsActive && p.IsEnabled)
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var compiled = new List<CompiledAuditPolicy>(rows.Count);
        foreach (var r in rows)
        {
            Regex regex;
            try
            {
                regex = new Regex(
                    r.EventCodePattern,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(
                    ex,
                    "AuditPolicy {Code} has invalid pattern; dropping from snapshot.",
                    r.Code);
                continue;
            }

            var redact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in r.ExtraRedactKeys ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    redact.Add(k);
                }
            }

            compiled.Add(new CompiledAuditPolicy(
                Code: r.Code,
                Module: r.Module,
                Screen: r.Screen,
                DataCategory: r.DataCategory,
                Regex: regex,
                OverrideSeverity: r.OverrideSeverity,
                SuppressAudit: r.SuppressAudit,
                ExtraRedactKeys: redact,
                Priority: r.Priority));
        }

        Interlocked.Exchange(ref _snapshot, compiled.ToArray());
    }

    /// <summary>
    /// Test seam — returns the current snapshot length. Used by integration tests to
    /// assert that <see cref="InvalidateAsync"/> picked up a newly inserted row.
    /// </summary>
    internal int SnapshotCount => _snapshot.Length;

    /// <summary>
    /// Returns <c>true</c> when the filter passes — either the caller didn't supply
    /// a value (null bypass) or the policy's value matches case-insensitively.
    /// </summary>
    /// <param name="policyValue">The value stored on the policy row.</param>
    /// <param name="callerValue">The value passed at resolve time; null means "ignore".</param>
    private static bool FilterMatches(string? policyValue, string? callerValue)
    {
        if (callerValue is null)
        {
            return true;
        }
        if (policyValue is null)
        {
            // A policy that does NOT specify a filter MATCHES any caller value for
            // that dimension. This is the dual of the null-caller bypass — without
            // this rule, a generic policy (Module="*"-shaped) would only match calls
            // that also omit the dimension, which would be surprising.
            return true;
        }
        return string.Equals(policyValue, callerValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Wrapper around <see cref="Regex.IsMatch(string)"/> that swallows
    /// <see cref="RegexMatchTimeoutException"/> — a pathological backtrack on a
    /// single event code returns <c>false</c> so the broken pattern cannot wedge
    /// the resolver. The 50 ms timeout is set at compile time in
    /// <see cref="InvalidateAsync"/>.
    /// </summary>
    /// <param name="rx">Compiled regex (timeout already set).</param>
    /// <param name="input">Candidate event-code string.</param>
    /// <returns><c>true</c> when the regex matches; <c>false</c> on no-match OR timeout.</returns>
    private static bool SafeIsMatch(Regex rx, string input)
    {
        try
        {
            return rx.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Compiled, snapshot-side projection of a single <see cref="AuditPolicy"/> row.
    /// The regex is pre-compiled and the redact-key set is materialised in a
    /// hash set so the hot-path resolve operation does not allocate.
    /// </summary>
    /// <param name="Code">Natural-key code (carried into the resolver output for diagnostics).</param>
    /// <param name="Module">Module filter; null means "any".</param>
    /// <param name="Screen">Screen filter; null means "any".</param>
    /// <param name="DataCategory">Data-category filter; null means "any".</param>
    /// <param name="Regex">Pre-compiled regex with 50 ms timeout.</param>
    /// <param name="OverrideSeverity">Optional severity override.</param>
    /// <param name="SuppressAudit">Suppression flag.</param>
    /// <param name="ExtraRedactKeys">Materialised redact-key hash set.</param>
    /// <param name="Priority">Resolution priority used for ordering at snapshot build.</param>
    private sealed record CompiledAuditPolicy(
        string Code,
        string Module,
        string Screen,
        string? DataCategory,
        Regex Regex,
        AuditSeverity? OverrideSeverity,
        bool SuppressAudit,
        IReadOnlySet<string> ExtraRedactKeys,
        int Priority);
}
