using System;
using System.Threading.Tasks;
using Cnas.Ps.Application.Helpdesk;
using Cnas.Ps.Application.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R2500 / TOR PIR 020-023 — Quartz job that fires every 5 minutes and
/// invokes <see cref="ISupportTicketSlaEvaluator.EvaluateAsync"/>. SLA
/// enforcement must run 24/7 — the job is registered under the
/// <c>Always</c> peak-hour profile so the off-peak gate never short-circuits
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency guard.</b>
/// <see cref="DisallowConcurrentExecutionAttribute"/> keeps two fires from
/// racing the same ticket set. The evaluator itself is idempotent (dedupe
/// on <c>(TicketId, EventKind)</c>) so the guard is belt-and-braces.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SupportTicketSlaEvaluationJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "support-ticket-sla-evaluation";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "support-ticket-sla-evaluation-trigger";

    /// <summary>Cron expression — every 5 minutes, on the minute boundary.</summary>
    public const string Cron = "0 */5 * * * ?";

    /// <summary>R2173 — stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = JobScheduleProfileRegistry.SupportTicketSlaEvaluation;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SupportTicketSlaEvaluationJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory.</param>
    /// <param name="logger">Structured logger.</param>
    public SupportTicketSlaEvaluationJob(
        IServiceScopeFactory scopes,
        ILogger<SupportTicketSlaEvaluationJob> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;

        using var scope = _scopes.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<ISupportTicketSlaEvaluator>();
        var result = await evaluator.EvaluateAsync(ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "SupportTicketSlaEvaluationJob recorded {Count} new SLA events.",
                result.Value);
        }
        else
        {
            _logger.LogWarning(
                "SupportTicketSlaEvaluationJob evaluator failed: {ErrorCode} {ErrorMessage}.",
                result.ErrorCode, result.ErrorMessage);
        }
    }
}
