using System;
using System.Threading.Tasks;
using Cnas.Ps.Application.Scheduling;
using Cnas.Ps.Application.UseCases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Cnas.Ps.Infrastructure.Jobs;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — Quartz job that fires once a day at
/// 03:00 UTC, picks every <see cref="Cnas.Ps.Core.Domain.RecurrentPaymentSchedule"/>
/// row with <c>IsActive=true</c> and <c>NextPaymentDate</c> on or before
/// today, and dispatches the corresponding monthly-allowance payments via
/// <see cref="IRecurrentPaymentSchedulerService.RunDueAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency guard.</b>
/// <see cref="DisallowConcurrentExecutionAttribute"/> keeps two fires from
/// racing the same set of schedules. The scheduler service advances
/// <c>NextPaymentDate</c> by one cadence step in the same write that
/// creates the <c>MPayOrder</c>, so a re-fire on the same day is a no-op
/// even without the guard.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class RecurrentPaymentJob : IJob
{
    /// <summary>Stable Quartz job identity.</summary>
    public const string JobIdentity = "recurrent-payment-dispatcher";

    /// <summary>Stable Quartz trigger identity.</summary>
    public const string TriggerIdentity = "recurrent-payment-dispatcher-trigger";

    /// <summary>Cron expression — every day at 03:00 UTC.</summary>
    public const string Cron = "0 0 3 * * ?";

    /// <summary>Stable job code consulted by the peak-hour gate.</summary>
    public const string JobCode = "RecurrentPaymentDispatcher";

    private readonly IServiceScopeFactory _scopes;
    private readonly IPeakHourGate _peakHourGate;
    private readonly ILogger<RecurrentPaymentJob> _logger;

    /// <summary>Constructs the job.</summary>
    /// <param name="scopes">DI scope factory used to resolve the scoped service per fire.</param>
    /// <param name="peakHourGate">Peak-hour gate consulted on every fire (Anytime by default).</param>
    /// <param name="logger">Structured logger for run summary lines.</param>
    public RecurrentPaymentJob(
        IServiceScopeFactory scopes,
        IPeakHourGate peakHourGate,
        ILogger<RecurrentPaymentJob> logger)
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
        var svc = scope.ServiceProvider.GetRequiredService<IRecurrentPaymentSchedulerService>();
        var result = await svc.RunDueAsync(ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "RecurrentPaymentJob dispatched {DispatchedCount} schedules.",
                result.Value);
        }
        else
        {
            _logger.LogWarning(
                "RecurrentPaymentJob run-due failed: {ErrorCode} {ErrorMessage}.",
                result.ErrorCode, result.ErrorMessage);
        }
    }
}
