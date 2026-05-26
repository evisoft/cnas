using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC20 — Execute automated procedures (scheduler or on-demand). MR 010-012.</summary>
public interface IAutomationService
{
    /// <summary>Runs the named automation now and returns when it completes (or after a timeout).</summary>
    Task<Result> RunNowAsync(string automationCode, string parametersJson, CancellationToken cancellationToken = default);

    /// <summary>Updates the cron schedule for an automation.</summary>
    Task<Result> ScheduleAsync(string automationCode, string cronExpression, CancellationToken cancellationToken = default);
}
