using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.PublicServices;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cnas.Ps.Infrastructure.Services.PublicServices;

/// <summary>
/// R0500 / TOR CF 01.02 / UC01 — implementation of
/// <see cref="IPublicKpiService"/>. Computes a depersonalised KPI
/// snapshot against the read-replica and serves it for 5 minutes before
/// recomputing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache semantics.</b> The first call after process start (or after
/// the cache window elapses) materialises a fresh
/// <see cref="PublicKpiSnapshotDto"/>; subsequent calls inside the
/// window return the same instance. A single
/// <see cref="System.Threading.SemaphoreSlim"/> serialises concurrent
/// recompute attempts so a thundering herd costs at most one DB sweep.
/// </para>
/// <para>
/// <b>Read-replica routed.</b> Marked
/// <see cref="LongRunningReportServiceAttribute"/> so the architecture
/// suite enforces that the service NEVER injects the writable
/// <c>ICnasDbContext</c>.
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as a singleton so the cache survives
/// across requests; the read-replica context is resolved at scan time
/// from the supplied scope factory (NOT captured at construction —
/// that would leak a scoped dependency into a singleton).
/// </para>
/// </remarks>
[LongRunningReportService]
public sealed class PublicKpiService : IPublicKpiService, IDisposable
{
    /// <summary>
    /// Releases the internal <see cref="SemaphoreSlim"/>. Called by the DI
    /// container at process shutdown; the singleton service lives for the
    /// whole process so this rarely runs outside of tests.
    /// </summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>Cache TTL — recompute at most once per <see cref="CacheWindow"/>.</summary>
    private static readonly TimeSpan CacheWindow = TimeSpan.FromMinutes(5);

    /// <summary>Last-30-days threshold used by the decisions-issued KPI.</summary>
    private static readonly TimeSpan DecisionsLookback = TimeSpan.FromDays(30);

    /// <summary>UTC clock — never <see cref="DateTime.UtcNow"/> directly.</summary>
    private readonly ICnasTimeProvider _clock;

    /// <summary>
    /// DI scope factory used by the recompute path to materialise a fresh
    /// scoped <see cref="IReadOnlyCnasDbContext"/> for the duration of one
    /// snapshot recompute. The singleton service intentionally NEVER
    /// captures a scoped DbContext directly.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Lock that serialises concurrent recompute attempts.</summary>
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    /// <summary>Most-recently computed snapshot; null until the first scan completes.</summary>
    private PublicKpiSnapshotDto? _snapshot;

    /// <summary>
    /// UTC instant the cached snapshot was produced; <see cref="DateTime.MinValue"/>
    /// before the first scan so the freshness check trivially recomputes.
    /// </summary>
    private DateTime _snapshotAtUtc = DateTime.MinValue;

    /// <summary>
    /// Constructs the singleton. Each recompute opens a fresh DI scope from
    /// <paramref name="scopeFactory"/> so the read-replica DbContext lives
    /// only for the duration of one snapshot computation.
    /// </summary>
    /// <param name="clock">Centralised UTC clock.</param>
    /// <param name="scopeFactory">DI scope factory used to resolve the read-replica DbContext per recompute.</param>
    public PublicKpiService(
        ICnasTimeProvider clock,
        IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _clock = clock;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Test-only constructor that takes a direct
    /// <see cref="IReadOnlyCnasDbContext"/>. Marked internal so production
    /// code cannot accidentally bypass the scope factory — the per-test
    /// CnasDbContext lifetime is managed by the test harness.
    /// </summary>
    /// <param name="clock">Centralised UTC clock.</param>
    /// <param name="readDb">Direct read-only DbContext for in-memory tests.</param>
    internal PublicKpiService(ICnasTimeProvider clock, IReadOnlyCnasDbContext readDb)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(readDb);
        _clock = clock;
        _scopeFactory = new TestScopeFactory(readDb);
    }

    /// <summary>
    /// Minimal <see cref="IServiceScopeFactory"/> shim used exclusively by
    /// the test-only constructor; returns the same wrapped DbContext on
    /// every scope and never actually disposes anything.
    /// </summary>
    private sealed class TestScopeFactory(IReadOnlyCnasDbContext db) : IServiceScopeFactory
    {
        private readonly IReadOnlyCnasDbContext _db = db;

        public IServiceScope CreateScope() => new TestScope(_db);

        private sealed class TestScope(IReadOnlyCnasDbContext db) : IServiceScope, IServiceProvider
        {
            private readonly IReadOnlyCnasDbContext _db = db;
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(IReadOnlyCnasDbContext) ? _db : null;
            public void Dispose() { /* No-op — test fixture owns the lifetime. */ }
        }
    }

    /// <inheritdoc />
    public async Task<Result<PublicKpiSnapshotDto>> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        // Fast path — cache hit. We capture local copies to avoid races between
        // the freshness check and the snapshot read.
        var capturedSnapshot = _snapshot;
        var capturedAtUtc = _snapshotAtUtc;
        var nowUtc = _clock.UtcNow;
        if (capturedSnapshot is not null && nowUtc - capturedAtUtc < CacheWindow)
        {
            Cnas.Ps.Infrastructure.Observability.CnasMeter.PublicKpiSnapshotCacheHit.Add(1);
            return Result<PublicKpiSnapshotDto>.Success(capturedSnapshot);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate so we don't recompute while another
            // caller just published a fresh snapshot.
            nowUtc = _clock.UtcNow;
            if (_snapshot is not null && nowUtc - _snapshotAtUtc < CacheWindow)
            {
                Cnas.Ps.Infrastructure.Observability.CnasMeter.PublicKpiSnapshotCacheHit.Add(1);
                return Result<PublicKpiSnapshotDto>.Success(_snapshot);
            }

            var fresh = await ComputeAsync(nowUtc, cancellationToken).ConfigureAwait(false);
            _snapshot = fresh;
            _snapshotAtUtc = nowUtc;
            Cnas.Ps.Infrastructure.Observability.CnasMeter.PublicKpiSnapshotComputed.Add(1);
            return Result<PublicKpiSnapshotDto>.Success(fresh);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs the underlying aggregate queries and packs the result into a
    /// <see cref="PublicKpiSnapshotDto"/>. Queries fan out to the read-replica
    /// via <see cref="IReadOnlyCnasDbContext"/>.
    /// </summary>
    /// <param name="nowUtc">UTC instant carried into the result and used for the 30-day window.</param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>The freshly computed snapshot.</returns>
    private async Task<PublicKpiSnapshotDto> ComputeAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReadOnlyCnasDbContext>();
        var thirtyDaysAgoUtc = nowUtc - DecisionsLookback;

        // Active contributors — IsActive && !IsDeactivated. Hard counts only;
        // no projection of row contents leaves the query.
        var contributors = await db.Contributors
            .Where(c => c.IsActive && !c.IsDeactivated)
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Active insured persons.
        var insured = await db.InsuredPersons
            .Where(p => p.IsActive)
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Pending applications — non-terminal lifecycle states.
        var pending = await db.Applications
            .Where(a => a.IsActive &&
                        (a.Status == ApplicationStatus.Submitted ||
                         a.Status == ApplicationStatus.UnderExamination ||
                         a.Status == ApplicationStatus.PendingApproval))
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Decisions issued in the last 30 days — proxied by application
        // transitions into terminal states.
        var decisions = await db.Applications
            .Where(a => (a.Status == ApplicationStatus.Approved ||
                         a.Status == ApplicationStatus.Rejected ||
                         a.Status == ApplicationStatus.Closed) &&
                        a.UpdatedAtUtc != null &&
                        a.UpdatedAtUtc >= thirtyDaysAgoUtc)
            .LongCountAsync(cancellationToken).ConfigureAwait(false);

        // Most-recent successful Treasury feed import — exposes a freshness
        // signal for the inbound contributions pipeline.
        var lastTreasuryImportAtUtc = await db.TreasuryFeedImports
            .Where(t => t.Status == TreasuryFeedImportStatus.Completed && t.CompletedAt != null)
            .OrderByDescending(t => t.CompletedAt)
            .Select(t => t.CompletedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return new PublicKpiSnapshotDto(
            ComputedAtUtc: nowUtc,
            TotalActiveContributors: contributors,
            TotalActiveInsuredPersons: insured,
            TotalPendingApplications: pending,
            DecisionsIssuedLast30Days: decisions,
            LastSuccessfulTreasuryImportAtUtc: lastTreasuryImportAtUtc);
    }
}
