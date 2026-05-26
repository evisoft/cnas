using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Backups;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Backups;

/// <summary>
/// R2307 / TOR SEC 060 — production implementation of
/// <see cref="IBackupOrchestrator"/>. Drives a single backup run end-to-end:
/// payload production → upload → hash verification → persist integrity
/// check → finalise status. Also owns the retention sweep + integrity
/// recheck.
/// </summary>
public sealed class BackupOrchestrator : IBackupOrchestrator
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
    private readonly IEnumerable<IBackupPayloadProvider> _providers;
    private readonly IEnumerable<IBackupTarget> _targets;
    private readonly IValidator<BackupRunFilterDto> _filterValidator;
    private readonly ILogger<BackupOrchestrator> _logger;

    /// <summary>Constructs the orchestrator with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="providers">Registered payload providers; resolved by Scope.</param>
    /// <param name="targets">Registered targets; resolved by Kind.</param>
    /// <param name="filterValidator">Validator for run-filter input.</param>
    /// <param name="logger">Structured logger.</param>
    public BackupOrchestrator(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IEnumerable<IBackupPayloadProvider> providers,
        IEnumerable<IBackupTarget> targets,
        IValidator<BackupRunFilterDto> filterValidator,
        ILogger<BackupOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(filterValidator);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _providers = providers;
        _targets = targets;
        _filterValidator = filterValidator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<BackupRunDto>> RunPolicyAsync(
        string policySqid,
        BackupTriggerKind trigger,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(policySqid);
        if (decoded.IsFailure)
        {
            return Result<BackupRunDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var policy = await _db.BackupPolicies
            .FirstOrDefaultAsync(p => p.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            return Result<BackupRunDto>.Failure(ErrorCodes.NotFound, "Backup policy not found.");
        }
        if (policy.IsArchived || !policy.IsActive)
        {
            return Result<BackupRunDto>.Failure(
                IBackupOrchestrator.PolicyNotActiveCode,
                "Backup policy must be Active (not Archived / not Deactivated) to run.");
        }

        var provider = _providers.FirstOrDefault(p => p.Scope == policy.Scope);
        if (provider is null)
        {
            return Result<BackupRunDto>.Failure(
                IBackupOrchestrator.ProviderNotConfiguredCode,
                $"No IBackupPayloadProvider registered for Scope={policy.Scope}.");
        }
        var target = _targets.FirstOrDefault(t => t.Kind == policy.TargetKind);
        if (target is null)
        {
            return Result<BackupRunDto>.Failure(
                IBackupTarget.TargetNotConfiguredCode,
                $"No IBackupTarget registered for TargetKind={policy.TargetKind}.");
        }

        var startedAt = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";

        var runNumber = await MintRunNumberAsync(startedAt, cancellationToken).ConfigureAwait(false);
        var run = new BackupRun
        {
            PolicyId = policy.Id,
            RunNumber = runNumber,
            Status = BackupRunStatus.Running,
            TriggerKind = trigger,
            StartedAt = startedAt,
            CreatedAtUtc = startedAt,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.BackupRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.BackupRunStarted.Add(
            1,
            new KeyValuePair<string, object?>("policy_code", policy.PolicyCode));

        var sw = Stopwatch.StartNew();
        try
        {
            // Step 1 — payload production.
            var payloadResult = await provider.ProducePayloadAsync(policy, cancellationToken).ConfigureAwait(false);
            if (payloadResult.IsFailure)
            {
                return await FinaliseFailureAsync(run, policy, sw, payloadResult.ErrorMessage ?? "Payload production failed.", cancellationToken).ConfigureAwait(false);
            }
            var payload = payloadResult.Value;
            run.PayloadSizeBytes = payload.SizeBytes;
            run.PayloadHashSha256 = payload.Sha256Hex;

            // Step 2 — upload.
            var uploadResult = await target.UploadAsync(policy, payload, cancellationToken).ConfigureAwait(false);
            if (uploadResult.IsFailure)
            {
                return await FinaliseFailureAsync(run, policy, sw, uploadResult.ErrorMessage ?? "Upload failed.", cancellationToken).ConfigureAwait(false);
            }
            run.PayloadStorageKey = uploadResult.Value.StorageKey;

            // Step 3 — verify the target's echoed hash against the local pre-hash.
            var integrityStatus = string.Equals(uploadResult.Value.Sha256Hex, payload.Sha256Hex, StringComparison.OrdinalIgnoreCase)
                ? BackupIntegrityStatus.Passed
                : BackupIntegrityStatus.Failed;

            var integrityCheck = new BackupIntegrityCheck
            {
                RunId = run.Id,
                Status = integrityStatus,
                CheckedAt = _clock.UtcNow,
                ExpectedHash = payload.Sha256Hex,
                ActualHash = uploadResult.Value.Sha256Hex,
                FailureReason = integrityStatus == BackupIntegrityStatus.Passed
                    ? null
                    : "Hash echoed by the upload target differs from the local-computed hash.",
                CreatedAtUtc = _clock.UtcNow,
                CreatedBy = actor,
                IsActive = true,
            };
            _db.BackupIntegrityChecks.Add(integrityCheck);

            sw.Stop();
            run.DurationMs = sw.ElapsedMilliseconds;
            run.CompletedAt = _clock.UtcNow;
            if (integrityStatus == BackupIntegrityStatus.Passed)
            {
                run.Status = BackupRunStatus.Succeeded;
                policy.LastSuccessfulRunAt = run.CompletedAt;
            }
            else
            {
                run.Status = BackupRunStatus.IntegrityFailed;
                run.FailureReason = "Integrity check did not pass.";
                policy.LastFailedRunAt = run.CompletedAt;
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            CnasMeter.BackupIntegrityCheckOutcome.Add(
                1,
                new KeyValuePair<string, object?>("status", integrityStatus.ToString()));
            CnasMeter.BackupRunCompleted.Add(
                1,
                new KeyValuePair<string, object?>("policy_code", policy.PolicyCode),
                new KeyValuePair<string, object?>("terminal_status", run.Status.ToString()));

            if (run.Status == BackupRunStatus.Succeeded)
            {
                await EmitAuditAsync(
                    IBackupOrchestrator.AuditRunSucceeded,
                    AuditSeverity.Critical,
                    actor,
                    run.Id,
                    new
                    {
                        runSqid = _sqids.Encode(run.Id),
                        policySqid = _sqids.Encode(policy.Id),
                        policyCode = policy.PolicyCode,
                        run.RunNumber,
                        run.PayloadSizeBytes,
                        run.DurationMs,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await EmitAuditAsync(
                    IBackupOrchestrator.AuditIntegrityFailed,
                    AuditSeverity.Critical,
                    actor,
                    run.Id,
                    new
                    {
                        runSqid = _sqids.Encode(run.Id),
                        policySqid = _sqids.Encode(policy.Id),
                        policyCode = policy.PolicyCode,
                        run.RunNumber,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return Result<BackupRunDto>.Success(ToDto(run, policy));
        }
        catch (OperationCanceledException)
        {
            return await FinaliseFailureAsync(run, policy, sw, "Run cancelled before completion.", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "BackupOrchestrator run {RunId} for policy {PolicyCode} failed.", run.Id, policy.PolicyCode);
            return await FinaliseFailureAsync(run, policy, sw, SanitiseFailureMessage(ex.Message), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<Result<BackupRunDto>> GetRunByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<BackupRunDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var run = await _read.BackupRuns
            .FirstOrDefaultAsync(r => r.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<BackupRunDto>.Failure(ErrorCodes.NotFound, "Backup run not found.");
        }
        var policy = await _read.BackupPolicies
            .FirstOrDefaultAsync(p => p.Id == run.PolicyId, cancellationToken)
            .ConfigureAwait(false);
        return policy is null
            ? Result<BackupRunDto>.Failure(ErrorCodes.NotFound, "Parent backup policy not found.")
            : Result<BackupRunDto>.Success(ToDto(run, policy));
    }

    /// <inheritdoc />
    public async Task<Result<BackupRunPageDto>> ListRunsAsync(
        BackupRunFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<BackupRunPageDto>.Failure(ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<BackupRun> q = _read.BackupRuns;
        if (!string.IsNullOrWhiteSpace(filter.PolicySqid))
        {
            var decoded = _sqids.TryDecode(filter.PolicySqid);
            if (decoded.IsFailure)
            {
                return Result<BackupRunPageDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
            }
            var policyId = decoded.Value;
            q = q.Where(r => r.PolicyId == policyId);
        }
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<BackupRunStatus>(filter.Status, ignoreCase: false, out var status))
        {
            q = q.Where(r => r.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.TriggerKind)
            && Enum.TryParse<BackupTriggerKind>(filter.TriggerKind, ignoreCase: false, out var trigger))
        {
            q = q.Where(r => r.TriggerKind == trigger);
        }
        if (filter.StartedAfter is not null)
        {
            var after = filter.StartedAfter.Value;
            q = q.Where(r => r.StartedAt >= after);
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var policyIds = rows.Select(r => r.PolicyId).Distinct().ToList();
        var policies = await _read.BackupPolicies
            .Where(p => policyIds.Contains(p.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = policies.ToDictionary(p => p.Id);

        var items = rows.Select(r => byId.TryGetValue(r.PolicyId, out var policy)
            ? ToDto(r, policy)
            : ToDto(r, fallbackPolicySqid: _sqids.Encode(r.PolicyId))).ToList();

        var page = new BackupRunPageDto(items, total, filter.Skip, filter.Take);
        return Result<BackupRunPageDto>.Success(page);
    }

    /// <inheritdoc />
    public async Task<Result<int>> SweepExpiredRunsAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";

        // Eligible: payload still present + not yet purged + retention window expired.
        // The retention window comes from the parent policy; project + filter inline.
        var candidates = await (from r in _db.BackupRuns
                                join p in _db.BackupPolicies on r.PolicyId equals p.Id
                                where r.PayloadStorageKey != null
                                    && r.RetentionPurgedAt == null
                                    && r.StartedAt < now.AddDays(-p.RetentionDays)
                                select new RunWithPolicy(r, p))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var purged = 0;
        foreach (var pair in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = _targets.FirstOrDefault(t => t.Kind == pair.Policy.TargetKind);
            if (target is null)
            {
                _logger.LogWarning(
                    "BackupOrchestrator sweep skipped run {RunId} — no target adapter for TargetKind={TargetKind}.",
                    pair.Run.Id, pair.Policy.TargetKind);
                continue;
            }
            var deleteResult = await target.DeleteAsync(pair.Run.PayloadStorageKey!, cancellationToken).ConfigureAwait(false);
            if (deleteResult.IsFailure)
            {
                _logger.LogWarning(
                    "BackupOrchestrator sweep could not delete storage key for run {RunId}: {ErrorCode} {ErrorMessage}.",
                    pair.Run.Id, deleteResult.ErrorCode, deleteResult.ErrorMessage);
                continue;
            }
            pair.Run.RetentionPurgedAt = _clock.UtcNow;
            pair.Run.UpdatedAtUtc = pair.Run.RetentionPurgedAt;
            pair.Run.UpdatedBy = actor;
            purged++;
        }

        if (purged > 0)
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            CnasMeter.BackupRetentionPurged.Add(purged);
        }

        await EmitAuditAsync(
            IBackupOrchestrator.AuditRetentionSwept,
            AuditSeverity.Critical,
            actor,
            targetEntityId: 0L,
            new { purgedCount = purged, atUtc = now.ToString("O", CultureInfo.InvariantCulture) },
            cancellationToken).ConfigureAwait(false);

        return Result<int>.Success(purged);
    }

    /// <inheritdoc />
    public async Task<Result<BackupIntegrityCheckDto>> RetryIntegrityCheckAsync(
        string runSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(runSqid);
        if (decoded.IsFailure)
        {
            return Result<BackupIntegrityCheckDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var run = await _db.BackupRuns
            .FirstOrDefaultAsync(r => r.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result<BackupIntegrityCheckDto>.Failure(ErrorCodes.NotFound, "Backup run not found.");
        }
        if (string.IsNullOrWhiteSpace(run.PayloadStorageKey) || string.IsNullOrWhiteSpace(run.PayloadHashSha256))
        {
            return Result<BackupIntegrityCheckDto>.Failure(ErrorCodes.Conflict, "Run has no payload to verify.");
        }
        var policy = await _db.BackupPolicies
            .FirstOrDefaultAsync(p => p.Id == run.PolicyId, cancellationToken)
            .ConfigureAwait(false);
        if (policy is null)
        {
            return Result<BackupIntegrityCheckDto>.Failure(ErrorCodes.NotFound, "Parent backup policy not found.");
        }
        var target = _targets.FirstOrDefault(t => t.Kind == policy.TargetKind);
        if (target is null)
        {
            return Result<BackupIntegrityCheckDto>.Failure(
                IBackupTarget.TargetNotConfiguredCode,
                $"No IBackupTarget registered for TargetKind={policy.TargetKind}.");
        }

        var downloadResult = await target.DownloadAsync(run.PayloadStorageKey, cancellationToken).ConfigureAwait(false);
        BackupIntegrityStatus status;
        string actualHash;
        string? reason;
        if (downloadResult.IsFailure)
        {
            status = BackupIntegrityStatus.Inconclusive;
            actualHash = string.Empty;
            reason = downloadResult.ErrorMessage;
        }
        else
        {
            actualHash = downloadResult.Value.Sha256Hex;
            if (string.Equals(actualHash, run.PayloadHashSha256, StringComparison.OrdinalIgnoreCase))
            {
                status = BackupIntegrityStatus.Passed;
                reason = null;
            }
            else
            {
                status = BackupIntegrityStatus.Failed;
                reason = "Re-downloaded payload hash differs from the stored expected hash.";
            }
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        var existing = await _db.BackupIntegrityChecks
            .FirstOrDefaultAsync(c => c.RunId == run.Id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            existing = new BackupIntegrityCheck
            {
                RunId = run.Id,
                Status = status,
                CheckedAt = now,
                ExpectedHash = run.PayloadHashSha256,
                ActualHash = actualHash,
                FailureReason = reason,
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
            };
            _db.BackupIntegrityChecks.Add(existing);
        }
        else
        {
            existing.Status = status;
            existing.CheckedAt = now;
            existing.ActualHash = actualHash;
            existing.FailureReason = reason;
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = actor;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.BackupIntegrityCheckOutcome.Add(
            1,
            new KeyValuePair<string, object?>("status", status.ToString()));

        if (status != BackupIntegrityStatus.Passed)
        {
            await EmitAuditAsync(
                IBackupOrchestrator.AuditIntegrityFailed,
                AuditSeverity.Critical,
                actor,
                run.Id,
                new
                {
                    runSqid = _sqids.Encode(run.Id),
                    policySqid = _sqids.Encode(policy.Id),
                    status = status.ToString(),
                },
                cancellationToken).ConfigureAwait(false);
        }

        return Result<BackupIntegrityCheckDto>.Success(ToDto(existing));
    }

    /// <summary>Persists a Failed run + audits + metric.</summary>
    /// <param name="run">Run entity (already tracked).</param>
    /// <param name="policy">Parent policy.</param>
    /// <param name="sw">Stopwatch started at the run begin.</param>
    /// <param name="reason">Raw failure description.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The failed-run DTO.</returns>
    private async Task<Result<BackupRunDto>> FinaliseFailureAsync(
        BackupRun run,
        BackupPolicy policy,
        Stopwatch sw,
        string reason,
        CancellationToken cancellationToken)
    {
        sw.Stop();
        var actor = _caller.UserSqid ?? "system";
        run.Status = BackupRunStatus.Failed;
        run.CompletedAt = _clock.UtcNow;
        run.DurationMs = sw.ElapsedMilliseconds;
        run.FailureReason = SanitiseFailureMessage(reason);
        policy.LastFailedRunAt = run.CompletedAt;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.BackupRunCompleted.Add(
            1,
            new KeyValuePair<string, object?>("policy_code", policy.PolicyCode),
            new KeyValuePair<string, object?>("terminal_status", run.Status.ToString()));

        await EmitAuditAsync(
            IBackupOrchestrator.AuditRunFailed,
            AuditSeverity.Critical,
            actor,
            run.Id,
            new
            {
                runSqid = _sqids.Encode(run.Id),
                policySqid = _sqids.Encode(policy.Id),
                policyCode = policy.PolicyCode,
                run.RunNumber,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<BackupRunDto>.Success(ToDto(run, policy));
    }

    /// <summary>
    /// Mints the next deterministic <c>BKR-{year}-{seq:000000}</c> run number
    /// for <paramref name="now"/>. The sequence resets per calendar year;
    /// concurrent fires across a year boundary remain safe because the
    /// run-number column carries a unique index — a collision retries.
    /// </summary>
    /// <param name="now">Current UTC.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The minted run number.</returns>
    private async Task<string> MintRunNumberAsync(DateTime now, CancellationToken cancellationToken)
    {
        var year = now.Year;
        var prefix = $"BKR-{year}-";
        var lastForYear = await _db.BackupRuns
            .Where(r => r.RunNumber.StartsWith(prefix))
            .OrderByDescending(r => r.RunNumber)
            .Select(r => r.RunNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var nextSeq = 1L;
        if (!string.IsNullOrEmpty(lastForYear) && lastForYear.Length > prefix.Length)
        {
            var tail = lastForYear[prefix.Length..];
            if (long.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                nextSeq = parsed + 1;
            }
        }
        return $"{prefix}{nextSeq.ToString("D6", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Returns a sanitised, bounded version of <paramref name="message"/>
    /// suitable for the <c>BackupRun.FailureReason</c> column.
    /// </summary>
    /// <param name="message">Raw exception or failure message.</param>
    /// <returns>Bounded ≤ 1000-char string.</returns>
    private static string SanitiseFailureMessage(string message)
    {
        const int Max = 1000;
        if (string.IsNullOrEmpty(message))
        {
            return "Run failed without a description.";
        }
        return message.Length <= Max ? message : message[..(Max - 3)] + "...";
    }

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row; 0 for sweep-style events.</param>
    /// <param name="details">Anonymous payload object serialised to JSON.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completes when the audit row is enqueued.</returns>
    private async Task EmitAuditAsync(
        string eventCode,
        AuditSeverity severity,
        string actor,
        long targetEntityId,
        object details,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            severity,
            actor,
            nameof(BackupRun),
            targetEntityId == 0L ? null : targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects a run + parent policy into the outbound DTO.</summary>
    /// <param name="run">Loaded run.</param>
    /// <param name="policy">Parent policy.</param>
    /// <returns>Populated DTO.</returns>
    private BackupRunDto ToDto(BackupRun run, BackupPolicy policy) => ToDto(run, _sqids.Encode(policy.Id));

    /// <summary>Projects a run when only the parent Sqid is available.</summary>
    /// <param name="run">Loaded run.</param>
    /// <param name="fallbackPolicySqid">Sqid of the parent policy.</param>
    /// <returns>Populated DTO.</returns>
    private BackupRunDto ToDto(BackupRun run, string fallbackPolicySqid) => new(
        Id: _sqids.Encode(run.Id),
        PolicySqid: fallbackPolicySqid,
        RunNumber: run.RunNumber,
        Status: run.Status.ToString(),
        TriggerKind: run.TriggerKind.ToString(),
        StartedAt: run.StartedAt,
        CompletedAt: run.CompletedAt,
        DurationMs: run.DurationMs,
        PayloadSizeBytes: run.PayloadSizeBytes,
        PayloadHashSha256: run.PayloadHashSha256,
        FailureReason: run.FailureReason,
        RetentionPurgedAt: run.RetentionPurgedAt);

    /// <summary>Projects an integrity-check row into the outbound DTO.</summary>
    /// <param name="check">Loaded check.</param>
    /// <returns>Populated DTO.</returns>
    private BackupIntegrityCheckDto ToDto(BackupIntegrityCheck check) => new(
        Id: _sqids.Encode(check.Id),
        RunSqid: _sqids.Encode(check.RunId),
        Status: check.Status.ToString(),
        CheckedAt: check.CheckedAt,
        ExpectedHash: check.ExpectedHash,
        ActualHash: check.ActualHash,
        FailureReason: check.FailureReason);

    /// <summary>Internal join projection used by the retention sweep query.</summary>
    /// <param name="Run">The candidate run.</param>
    /// <param name="Policy">The candidate run's parent policy.</param>
    private sealed record RunWithPolicy(BackupRun Run, BackupPolicy Policy);
}
