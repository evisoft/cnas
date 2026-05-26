using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R2003 / R0133 — Quartz job that runs the template-language coverage scan
/// on a daily off-peak cadence. Invokes
/// <see cref="ITemplateLanguageCoverageService.RecordCoverageRunAsync"/> with
/// the canonical default filter (<c>OnlyApproved=true</c>,
/// <c>IncludeRetiredTemplates=false</c>) so the persisted gap registry stays
/// in lock-step with the production rendering pipeline's expectations.
/// </summary>
/// <remarks>
/// <para>
/// <b>Peak-hour gate.</b> The job's profile is
/// <c>JobScheduleProfileMode.OffPeakOnly</c>; a fire that lands in a peak
/// window short-circuits without invoking the service.
/// </para>
/// <para>
/// <b>Concurrency guard.</b> <see cref="DisallowConcurrentExecutionAttribute"/>
/// prevents two fires from racing — the projection is read-heavy and the
/// finding inserts are deduped via the filtered unique index anyway, but the
/// guard keeps the audit log clean of duplicate scan headers.
/// </para>
/// <para>
/// <b>Cadence.</b> Daily at 03:45 UTC — chosen to land cleanly between the
/// existing 03:00 IntegrityCheck sweep and the 04:00 TreasuryFeedImport
/// fire so the off-peak window is fully utilised without overlap.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class TemplateLanguageCoverageScanJob : IJob
{
    /// <summary>Stable Quartz job identity used for registration and lookups.</summary>
    public const string JobIdentity = "template-language-coverage-scan";

    /// <summary>Stable Quartz trigger identity paired with <see cref="JobIdentity"/>.</summary>
    public const string TriggerIdentity = "template-language-coverage-scan-trigger";

    /// <summary>Cron expression — daily at 03:45 UTC.</summary>
    public const string Cron = "0 45 3 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate (OffPeakOnly profile).</summary>
    public const string JobCode = JobScheduleProfileRegistry.TemplateLanguageCoverageScan;

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<TemplateLanguageCoverageScanJob> _logger;

    /// <summary>Constructs the job with its collaborators.</summary>
    /// <param name="scopes">Scope factory used to resolve scoped collaborators per fire.</param>
    /// <param name="peakHourGate">R2173 peak-hour gate consulted at the top of each fire.</param>
    /// <param name="logger">Structured logger.</param>
    /// <exception cref="ArgumentNullException">When a required collaborator is null.</exception>
    public TemplateLanguageCoverageScanJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<TemplateLanguageCoverageScanJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(peakHourGate);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _peakHourGate = peakHourGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        // R2173 / TOR PSR 004 — peak-hour gate. OffPeakOnly profile.
        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITemplateLanguageCoverageService>();

        try
        {
            // Canonical filter — RO / EN / RU, only approved variants count as
            // coverage, retired templates excluded. The page bounds keep the
            // returned report bounded (the service still scans every template
            // for the persistence path).
            var filter = new TemplateLanguageCoverageFilterDto(
                RequiredLanguages: null,
                OnlyApproved: true,
                IncludeRetiredTemplates: false,
                Skip: 0,
                Take: 100);

            var result = await service.RecordCoverageRunAsync(filter, ct).ConfigureAwait(false);
            if (result.IsFailure)
            {
                _logger.LogError(
                    "TemplateLanguageCoverageScanJob failed (code={Code}, message={Message}).",
                    result.ErrorCode, result.ErrorMessage);
                await EmitJobFailureAuditAsync(scope.ServiceProvider, result.ErrorMessage ?? "(no message)", ct)
                    .ConfigureAwait(false);
                return;
            }

            _logger.LogInformation(
                "TemplateLanguageCoverageScanJob completed (scanned={Scanned}, fullyCovered={Covered}, withGaps={Gaps}).",
                result.Value.TotalTemplatesScanned,
                result.Value.TotalTemplatesFullyCovered,
                result.Value.TotalTemplatesWithGaps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await EmitJobFailureAuditAsync(scope.ServiceProvider, ex.GetType().Name + ": " + ex.Message, ct)
                .ConfigureAwait(false);
            _logger.LogError(ex, "TemplateLanguageCoverageScanJob crashed.");
        }
    }

    /// <summary>
    /// Writes a Critical <c>TEMPLATE.COVERAGE.JOB_FAILED</c> audit row when
    /// the scan pipeline crashed before the service could finalise the
    /// projection. Defensive — never throws back to Quartz.
    /// </summary>
    /// <param name="sp">DI scope's service provider.</param>
    /// <param name="reason">Human-readable reason persisted to the audit details.</param>
    /// <param name="ct">Cancellation propagated from the fire.</param>
    private static async Task EmitJobFailureAuditAsync(IServiceProvider sp, string reason, CancellationToken ct)
    {
        try
        {
            var audit = sp.GetRequiredService<IAuditService>();
            var details = JsonSerializer.Serialize(new { reason });
            await audit.RecordAsync(
                "TEMPLATE.COVERAGE.JOB_FAILED",
                AuditSeverity.Critical,
                actorId: "system",
                targetEntity: nameof(TemplateLanguageCoverageFinding),
                targetEntityId: null,
                detailsJson: details,
                sourceIp: null,
                correlationId: null,
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Defence-in-depth — never throw out to Quartz from an audit emit.
        }
    }
}
