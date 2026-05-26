using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.ExternalSources;

/// <summary>
/// R0203 / TOR CF 20.06 — orchestrator over the per-source ingestion
/// framework. The service inserts a parent <c>ExternalSourceIngestionRun</c>
/// row, dispatches to the matching <see cref="IExternalSourceConnector"/>,
/// captures the per-source counters, and finalises the lifecycle. Both the
/// admin REST surface (TriggerKind=Manual) and the per-source Quartz jobs
/// (TriggerKind=Scheduled) drive this entry point.
/// </summary>
public interface IExternalSourceIngestionService
{
    /// <summary>Stable audit event code emitted on a successful run completion (Information severity).</summary>
    public const string AuditRunCompleted = "EXT_SRC.RUN.COMPLETED";

    /// <summary>Stable audit event code emitted on a run failure (Critical severity).</summary>
    public const string AuditRunFailed = "EXT_SRC.RUN.FAILED";

    /// <summary>Stable audit event code emitted when an admin manually triggers a run (Critical severity).</summary>
    public const string AuditManualTrigger = "EXT_SRC.RUN.MANUAL_TRIGGERED";

    /// <summary>Stable failure code returned when the supplied source code does not match any registered connector.</summary>
    public const string UnknownSourceCode = "EXT_SRC.UNKNOWN_SOURCE";

    /// <summary>
    /// Triggers an ingestion run on behalf of an authenticated admin. Emits
    /// a Critical-severity audit row at trigger time so the action is
    /// traceable even when the run subsequently fails.
    /// </summary>
    /// <param name="sourceCode">Upper-case source-system code.</param>
    /// <param name="asOfDate">Optional as-of date. When null the service substitutes today (UTC).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the persisted run DTO; on failure a typed Result.</returns>
    Task<Result<ExternalSourceIngestionRunDto>> TriggerManualRunAsync(
        string sourceCode,
        DateOnly? asOfDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers an ingestion run on behalf of the per-source Quartz job. The
    /// audit row is Information-severity since there is no human actor; the
    /// job itself supplies the as-of date (typically today).
    /// </summary>
    /// <param name="sourceCode">Upper-case source-system code.</param>
    /// <param name="asOfDate">Calendar date the run should target.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>On success the persisted run DTO; on failure a typed Result.</returns>
    Task<Result<ExternalSourceIngestionRunDto>> TriggerScheduledRunAsync(
        string sourceCode,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single run by its Sqid.
    /// </summary>
    /// <param name="runSqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>200 on hit; <see cref="ErrorCodes.NotFound"/> when not found.</returns>
    Task<Result<ExternalSourceIngestionRunDto>> GetRunByIdAsync(
        string runSqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists runs matching the supplied filter envelope.
    /// </summary>
    /// <param name="filter">Filter envelope (source / status / trigger / paging).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The paged DTO envelope.</returns>
    Task<Result<ExternalSourceIngestionRunPageDto>> ListRunsAsync(
        ExternalSourceIngestionRunFilterDto filter,
        CancellationToken cancellationToken = default);
}
