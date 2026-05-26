using System;
using System.Text.Json;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.ServiceManagement;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R2506 / TOR PIR 037-040 — Quartz job that runs daily at 04:15 UTC and
/// emits a <c>QA_RISK.REVIEW_OVERDUE</c> Information-severity audit row for
/// every quality risk whose <c>LastReviewedAt</c> is null or older than 365
/// days. Honours the peak-hour gate (Always profile — risk-review reminders
/// do not need an off-peak window).
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency guard.</b>
/// <see cref="DisallowConcurrentExecutionAttribute"/> keeps two fires from
/// racing the same row-set. The job is read-only over the registry and
/// idempotent at the per-fire granularity (it always emits one audit row per
/// overdue risk on each fire).
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class QualityRiskReviewSweepJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "quality-risk-review-sweep";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "quality-risk-review-sweep-trigger";

    /// <summary>Cron expression — daily at 04:15 UTC.</summary>
    public const string Cron = "0 15 4 * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.QualityRiskReviewSweep;

    /// <summary>Cached serializer options for the audit payload.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<QualityRiskReviewSweepJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory.</param>
    /// <param name="peakHourGate">Peak-hour gate.</param>
    /// <param name="logger">Structured logger.</param>
    public QualityRiskReviewSweepJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<QualityRiskReviewSweepJob> logger)
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

        if (await _peakHourGate.EvaluateAsync(JobCode, ct).ConfigureAwait(false) == PeakHourGateDecision.Skip)
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IQualityRiskService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var result = await service.ListOverdueForReviewAsync(
            IQualityRiskService.DefaultReviewWindowDays, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogWarning(
                "QualityRiskReviewSweepJob list failed: {ErrorCode} {ErrorMessage}.",
                result.ErrorCode, result.ErrorMessage);
            return;
        }

        foreach (var risk in result.Value)
        {
            ct.ThrowIfCancellationRequested();
            CnasMeter.QualityRiskReviewOverdueDetected.Add(1);

            // No PII — only the SCREAMING_SNAKE_CASE risk code and the sqid.
            var detailsJson = JsonSerializer.Serialize(
                new { riskSqid = risk.Id, riskCode = risk.RiskCode },
                CachedJsonOptions);

            await audit.RecordAsync(
                IQualityRiskService.AuditRiskReviewOverdue,
                AuditSeverity.Information,
                "system",
                nameof(QualityRisk),
                targetEntityId: null,
                detailsJson,
                sourceIp: null,
                correlationId: null,
                ct).ConfigureAwait(false);
        }
    }
}
