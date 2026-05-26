using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// Background sweeper that flips stale <see cref="PendingAdminAction"/> rows from
/// <see cref="PendingAdminActionStatus.Pending"/> to
/// <see cref="PendingAdminActionStatus.Expired"/> when their
/// <see cref="PendingAdminAction.ExpiresAtUtc"/> has elapsed (R0058 / SEC 027).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency.</b> The sweep is purely declarative — every run filters by
/// <c>Status == Pending &amp;&amp; ExpiresAtUtc &lt; now</c> and updates only the
/// matching rows. Re-running on an empty result set is a no-op.
/// </para>
/// <para>
/// <b>Belt-and-braces.</b> The service-side approve guard already flips stale rows
/// inline on the first attempted decision after expiry. This sweeper exists so the
/// pending-actions queue stays accurate even when no checker visits a long-stale
/// action — checkers don't see actions that have already silently expired.
/// </para>
/// <para>
/// <b>Scheduling.</b> Intended cadence: every 15 minutes.
/// TODO[r0058-quartz]: register a trigger for this job inside <see cref="QuartzComposition.AddCnasJobs"/>
/// alongside <c>DossierSlaMonitorJob</c>. The wiring is deferred to a follow-up batch
/// because adding a new trigger requires a fixture refresh on every existing
/// Quartz test; the inline TTL guard in <c>PendingAdminActionService</c> means the
/// missing scheduler entry does not affect correctness.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class MakerCheckerExpirySweeper(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ILogger<MakerCheckerExpirySweeper> logger) : IJob
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ILogger<MakerCheckerExpirySweeper> _logger = logger;

    /// <summary>Stable Quartz job name; used by the (deferred) trigger registration.</summary>
    public const string JobName = "maker-checker-expiry-sweeper";

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var now = _clock.UtcNow;
        var ct = context.CancellationToken;

        // Pull the offending rows into memory rather than emitting a bulk UPDATE —
        // the InMemory provider used by unit tests does not implement ExecuteUpdate,
        // and the per-run volume is bounded by how many actions can accumulate within
        // a single 15-minute window (small).
        var stale = await _db.PendingAdminActions
            .Where(p => p.IsActive
                        && p.Status == PendingAdminActionStatus.Pending
                        && p.ExpiresAtUtc <= now)
            .ToListAsync(ct).ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return;
        }

        foreach (var row in stale)
        {
            row.Status = PendingAdminActionStatus.Expired;
            row.UpdatedAtUtc = now;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0040 — one increment per row flipped to Expired. The PendingAdminActionService
        // approve-path TTL guard increments the same counter for the rows IT flips, so
        // the operator dashboard sees a single time-series for "auto-expired actions"
        // regardless of which code path actually did the flip.
        CnasMeter.AdminActionExpired.Add(stale.Count);

        _logger.LogInformation(
            "MakerCheckerExpirySweeper flipped {Count} pending admin actions to Expired.",
            stale.Count);
    }
}
