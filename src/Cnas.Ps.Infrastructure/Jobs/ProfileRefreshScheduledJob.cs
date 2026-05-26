using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ContributorProfileUpdates;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R0363 / TOR UC13 strategy 3 — Quartz job that batch-refreshes contributor profiles
/// from the configured upstream sources. Implementation lives in the codebase so the
/// scheduling code path can be unit-tested in isolation, but the job is NOT registered
/// with Quartz unless <see cref="ProfileRefreshOptions.EnableScheduledRefresh"/> is
/// <c>true</c>. The default is <c>false</c> — operators must explicitly opt in once the
/// NDA-gated RSP / RSUD / SI SFS WSDLs are wired through MConnect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-tick contract.</b> The job streams up to
/// <see cref="ProfileRefreshOptions.MaxContributorsPerRun"/> contributor primary keys
/// from the database in ascending order, then calls
/// <see cref="IProfileRefreshService.RefreshFromSourceAsync"/> for each — sequentially,
/// so that any per-call rate limit in the gateway resilience pipeline (R0104) applies
/// naturally without job-side throttling.
/// </para>
/// <para>
/// <b>Default cron.</b> <c>0 5 3 * * ?</c> — daily at 03:05 UTC. The hour was picked to
/// minimise overlap with upstream peak hours and to land before the morning operator
/// shift sees stale data.
/// </para>
/// <para>
/// <b>Source selection.</b> The job today only triggers <c>RSP</c> per tick — the
/// integrations against RSUD and SI SFS are emitted via on-demand controller endpoints
/// because their refresh rhythm differs (salary declarations are quarterly, not daily).
/// A future <c>Sources</c> options array can drive multi-source per-tick fan-out
/// without touching the contract surface.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class ProfileRefreshScheduledJob(
    IReadOnlyCnasDbContext db,
    IProfileRefreshService refresh,
    IOptions<ProfileRefreshOptions> options,
    ILogger<ProfileRefreshScheduledJob> logger) : IJob
{
    private readonly IReadOnlyCnasDbContext _db = db;
    private readonly IProfileRefreshService _refresh = refresh;
    private readonly ProfileRefreshOptions _options = options.Value;
    private readonly ILogger<ProfileRefreshScheduledJob> _logger = logger;

    /// <summary>Stable Quartz job name. Used by the deferred trigger registration.</summary>
    public const string JobName = "profile-refresh-scheduled";

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.EnableScheduledRefresh)
        {
            _logger.LogInformation(
                "ProfileRefreshScheduledJob skipped — EnableScheduledRefresh is false.");
            return;
        }

        var ct = context.CancellationToken;
        var cap = Math.Max(1, _options.MaxContributorsPerRun);

        var contributorIds = await _db.InsuredPersons
            .Where(p => p.IsActive)
            .OrderBy(p => p.Id)
            .Take(cap)
            .Select(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "ProfileRefreshScheduledJob processing {Count} contributors.",
            contributorIds.Count);

        foreach (var id in contributorIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _ = await _refresh.RefreshFromSourceAsync(
                    ProfileRefreshService.SourceRsp, id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-contributor failures must not abort the loop — the refresh service
                // already persists the failed run row, so each contributor is independent.
                _logger.LogWarning(ex,
                    "ProfileRefreshScheduledJob threw while refreshing contributor {Id}.", id);
            }
        }
    }
}
