using System.Globalization;
using System.Linq;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ExternalSources;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.ExternalSources;

/// <summary>
/// R0203 / TOR CF 20.06 — production implementation of
/// <see cref="IExternalSourceIngestionService"/>. Drives the lifecycle of one
/// <see cref="ExternalSourceIngestionRun"/> row from <c>Pending</c> through to
/// a terminal state and projects the connector outcome onto the per-source
/// counters.
/// </summary>
public sealed class ExternalSourceIngestionService : IExternalSourceIngestionService
{
    /// <summary>Cached JSON serializer options shared across audit payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IEnumerable<IExternalSourceConnector> _connectors;
    private readonly InMemoryExternalSourceConnector _fallback;
    private readonly IValidator<ExternalSourceManualTriggerInputDto> _triggerValidator;
    private readonly IValidator<ExternalSourceIngestionRunFilterDto> _filterValidator;
    private readonly ILogger<ExternalSourceIngestionService> _logger;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md RULE 4).</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="connectors">All registered <see cref="IExternalSourceConnector"/>.</param>
    /// <param name="fallback">In-memory connector used when no concrete connector matches.</param>
    /// <param name="triggerValidator">Validator for the manual-trigger input envelope.</param>
    /// <param name="filterValidator">Validator for the runs-list filter envelope.</param>
    /// <param name="logger">Structured logger.</param>
    public ExternalSourceIngestionService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IEnumerable<IExternalSourceConnector> connectors,
        InMemoryExternalSourceConnector fallback,
        IValidator<ExternalSourceManualTriggerInputDto> triggerValidator,
        IValidator<ExternalSourceIngestionRunFilterDto> filterValidator,
        ILogger<ExternalSourceIngestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(triggerValidator);
        ArgumentNullException.ThrowIfNull(filterValidator);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _connectors = connectors;
        _fallback = fallback;
        _triggerValidator = triggerValidator;
        _filterValidator = filterValidator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ExternalSourceIngestionRunDto>> TriggerManualRunAsync(
        string sourceCode,
        DateOnly? asOfDate,
        CancellationToken cancellationToken = default)
    {
        var input = new ExternalSourceManualTriggerInputDto(sourceCode, asOfDate);
        var v = await _triggerValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ExternalSourceIngestionRunDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }
        var todayUtc = _clock.TodayUtc;
        var dateViolation = ExternalSourceManualTriggerInputValidator.ValidateAsOfDate(asOfDate, todayUtc);
        if (dateViolation is not null)
        {
            return Result<ExternalSourceIngestionRunDto>.Failure(ErrorCodes.ValidationFailed, dateViolation);
        }

        var actor = _caller.UserSqid ?? "admin";
        await _audit.RecordAsync(
            IExternalSourceIngestionService.AuditManualTrigger,
            AuditSeverity.Critical,
            actor,
            nameof(ExternalSourceIngestionRun),
            null,
            JsonSerializer.Serialize(new
            {
                sourceCode,
                asOfDate = (asOfDate ?? todayUtc).ToString("O", CultureInfo.InvariantCulture),
                trigger = ExternalSourceTriggerKind.Manual.ToString(),
            }, CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return await ExecuteRunAsync(
            sourceCode,
            asOfDate ?? todayUtc,
            ExternalSourceTriggerKind.Manual,
            actor,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Result<ExternalSourceIngestionRunDto>> TriggerScheduledRunAsync(
        string sourceCode,
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCode);
        var actor = _caller.UserSqid ?? "system";
        return ExecuteRunAsync(sourceCode, asOfDate, ExternalSourceTriggerKind.Scheduled, actor, cancellationToken);
    }

    /// <summary>
    /// Drives one ingestion run end-to-end: insert the parent row, dispatch
    /// to the matching connector, persist the per-source counters, emit
    /// audits + metrics.
    /// </summary>
    /// <param name="sourceCode">Upper-case source-system code.</param>
    /// <param name="asOfDate">As-of date the run should target.</param>
    /// <param name="trigger">Origin of the run.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The persisted run DTO regardless of terminal status.</returns>
    private async Task<Result<ExternalSourceIngestionRunDto>> ExecuteRunAsync(
        string sourceCode,
        DateOnly asOfDate,
        ExternalSourceTriggerKind trigger,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var runNumber = await GenerateRunNumberAsync(now.Year, cancellationToken).ConfigureAwait(false);
        var run = new ExternalSourceIngestionRun
        {
            SourceCode = sourceCode,
            RunNumber = runNumber,
            Status = ExternalSourceIngestionStatus.Pending,
            TriggerKind = trigger,
            StartedAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.ExternalSourceIngestionRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.ExternalSourceIngestionRunStarted.Add(
            1,
            new KeyValuePair<string, object?>("source_code", sourceCode),
            new KeyValuePair<string, object?>("trigger_kind", trigger.ToString()));

        // Flip Running and persist before dispatching to the connector so the
        // admin list shows the row mid-flight even on a long upstream call.
        run.Status = ExternalSourceIngestionStatus.Running;
        run.UpdatedAtUtc = _clock.UtcNow;
        run.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var connector = SelectConnector(sourceCode);
        var fetch = await DispatchAsync(connector, sourceCode, asOfDate, cancellationToken).ConfigureAwait(false);

        var completedAt = _clock.UtcNow;
        if (fetch.IsFailure)
        {
            run.Status = ExternalSourceIngestionStatus.Failed;
            run.CompletedAtUtc = completedAt;
            run.FailureReason = Truncate($"{fetch.ErrorCode}: {fetch.ErrorMessage}", 1000);
            run.UpdatedAtUtc = completedAt;
            run.UpdatedBy = actor;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await EmitRunAuditAsync(run, AuditSeverity.Critical,
                IExternalSourceIngestionService.AuditRunFailed,
                actor, fetch.ErrorCode, cancellationToken).ConfigureAwait(false);

            CnasMeter.ExternalSourceIngestionRunCompleted.Add(
                1,
                new KeyValuePair<string, object?>("source_code", sourceCode),
                new KeyValuePair<string, object?>("terminal_status", run.Status.ToString()));

            _logger.LogWarning(
                "ExternalSourceIngestionRun {RunNumber} failed for {SourceCode}: {ErrorCode}.",
                run.RunNumber, sourceCode, fetch.ErrorCode);

            return Result<ExternalSourceIngestionRunDto>.Success(ToDto(run));
        }

        var outcome = fetch.Value;
        run.Status = ExternalSourceIngestionStatus.Completed;
        run.CompletedAtUtc = completedAt;
        run.TotalRecordsPulled = outcome.RecordsPulled;
        run.TotalRecordsApplied = outcome.RecordsApplied;
        run.TotalRecordsSkipped = outcome.RecordsSkipped;
        run.TotalRecordsFailed = outcome.RecordsFailed;
        run.UpstreamPullId = Truncate(outcome.UpstreamPullId, 128);
        run.UpdatedAtUtc = completedAt;
        run.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitRunAuditAsync(run, AuditSeverity.Information,
            IExternalSourceIngestionService.AuditRunCompleted,
            actor, errorCode: null, cancellationToken).ConfigureAwait(false);

        CnasMeter.ExternalSourceIngestionRunCompleted.Add(
            1,
            new KeyValuePair<string, object?>("source_code", sourceCode),
            new KeyValuePair<string, object?>("terminal_status", run.Status.ToString()));

        return Result<ExternalSourceIngestionRunDto>.Success(ToDto(run));
    }

    /// <inheritdoc />
    public async Task<Result<ExternalSourceIngestionRunDto>> GetRunByIdAsync(
        string runSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(runSqid);
        if (decoded.IsFailure)
        {
            return Result<ExternalSourceIngestionRunDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.ExternalSourceIngestionRuns
            .FirstOrDefaultAsync(r => r.Id == decoded.Value && r.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<ExternalSourceIngestionRunDto>.Failure(
                ErrorCodes.NotFound, "External-source ingestion run not found.")
            : Result<ExternalSourceIngestionRunDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<ExternalSourceIngestionRunPageDto>> ListRunsAsync(
        ExternalSourceIngestionRunFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<ExternalSourceIngestionRunPageDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<ExternalSourceIngestionRun> q = _read.ExternalSourceIngestionRuns
            .Where(r => r.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.SourceCode))
        {
            q = q.Where(r => r.SourceCode == filter.SourceCode);
        }
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<ExternalSourceIngestionStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.TriggerKind)
            && Enum.TryParse<ExternalSourceTriggerKind>(filter.TriggerKind, ignoreCase: false, out var trigger))
        {
            q = q.Where(r => r.TriggerKind == trigger);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(r => r.StartedAtUtc)
            .ThenByDescending(r => r.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new ExternalSourceIngestionRunPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<ExternalSourceIngestionRunPageDto>.Success(page);
    }

    /// <summary>
    /// Picks the concrete connector matching <paramref name="sourceCode"/>;
    /// falls back to the in-memory connector when none is registered. The
    /// fallback path keeps the chart usable in dev / test without per-source
    /// configuration.
    /// </summary>
    /// <param name="sourceCode">Upper-case source-system code.</param>
    /// <returns>A connector instance (never null).</returns>
    private IExternalSourceConnector SelectConnector(string sourceCode)
    {
        foreach (var c in _connectors)
        {
            if (string.Equals(c.SourceCode, sourceCode, StringComparison.Ordinal))
            {
                return c;
            }
        }
        return _fallback;
    }

    /// <summary>
    /// Dispatches to the chosen connector. When the chosen connector is the
    /// in-memory fallback the source-aware overload is used so test fixtures
    /// stay per-source.
    /// </summary>
    /// <param name="connector">Selected connector.</param>
    /// <param name="sourceCode">Upper-case source-system code.</param>
    /// <param name="asOfDate">As-of date.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Result of the connector call.</returns>
    private static Task<Result<ExternalSourceFetchOutcomeDto>> DispatchAsync(
        IExternalSourceConnector connector,
        string sourceCode,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        if (connector is InMemoryExternalSourceConnector inMemory)
        {
            return inMemory.FetchAsync(sourceCode, asOfDate, cancellationToken);
        }
        return connector.FetchAsync(asOfDate, cancellationToken);
    }

    /// <summary>
    /// Generates the next per-year run-number using the existing rows as the
    /// sequence source. Format: <c>ESI-{year}-{seq:000000}</c>.
    /// </summary>
    /// <param name="year">Calendar year.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>The next run number.</returns>
    private async Task<string> GenerateRunNumberAsync(int year, CancellationToken cancellationToken)
    {
        var prefix = $"ESI-{year}-";
        var existingCount = await _db.ExternalSourceIngestionRuns
            .Where(r => r.RunNumber.StartsWith(prefix))
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        return $"{prefix}{(existingCount + 1):000000}";
    }

    /// <summary>Emits the lifecycle audit row with a PII-free details payload.</summary>
    /// <param name="run">Persisted run row.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="eventCode">Stable audit event code.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="errorCode">Stable error code on failure path; null on success.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>A completed Task.</returns>
    private Task EmitRunAuditAsync(
        ExternalSourceIngestionRun run,
        AuditSeverity severity,
        string eventCode,
        string actor,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            runSqid = _sqids.Encode(run.Id),
            run.RunNumber,
            run.SourceCode,
            status = run.Status.ToString(),
            trigger = run.TriggerKind.ToString(),
            run.TotalRecordsPulled,
            run.TotalRecordsApplied,
            run.TotalRecordsSkipped,
            run.TotalRecordsFailed,
            errorCode,
        }, CachedJsonOptions);
        return _audit.RecordAsync(
            eventCode,
            severity,
            actor,
            nameof(ExternalSourceIngestionRun),
            run.Id,
            payload,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken);
    }

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="run">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private ExternalSourceIngestionRunDto ToDto(ExternalSourceIngestionRun run) => new(
        Id: _sqids.Encode(run.Id),
        RunNumber: run.RunNumber,
        SourceCode: run.SourceCode,
        Status: run.Status.ToString(),
        TriggerKind: run.TriggerKind.ToString(),
        StartedAtUtc: run.StartedAtUtc,
        CompletedAtUtc: run.CompletedAtUtc,
        TotalRecordsPulled: run.TotalRecordsPulled,
        TotalRecordsApplied: run.TotalRecordsApplied,
        TotalRecordsSkipped: run.TotalRecordsSkipped,
        TotalRecordsFailed: run.TotalRecordsFailed,
        FailureReason: run.FailureReason,
        UpstreamPullId: run.UpstreamPullId);

    /// <summary>Truncates a string to the supplied cap (no ellipsis), preserving null/empty.</summary>
    /// <param name="value">Original string.</param>
    /// <param name="max">Maximum length.</param>
    /// <returns>Truncated string.</returns>
    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);
}
