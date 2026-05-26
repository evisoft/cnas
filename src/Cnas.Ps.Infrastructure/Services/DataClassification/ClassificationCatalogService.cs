using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.DataClassification;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.DataClassification;

/// <summary>
/// R2279 / TOR SEC 033 — production implementation of
/// <see cref="IClassificationCatalogService"/>. Owns the manual + scheduled
/// snapshot capture, the per-snapshot lookups, the idempotent drift
/// computation, and the finding acknowledgement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit strategy.</b> Snapshot captures + drift detections + finding
/// acknowledgements emit a single Critical audit row each. Drift detections
/// emit ONE audit per (baseline, current) pair carrying the total finding
/// count — not one audit per finding — to avoid spamming the security trail
/// when a large refactor introduces dozens of label changes simultaneously.
/// </para>
/// <para>
/// <b>Idempotent drift.</b> The drift computation is idempotent: when at
/// least one drift finding already exists for the supplied
/// (baseline, current) pair the method returns the persisted rows verbatim
/// and does NOT re-audit.
/// </para>
/// </remarks>
public sealed class ClassificationCatalogService : IClassificationCatalogService
{
    /// <summary>Stable audit code emitted on snapshot capture.</summary>
    public const string AuditSnapshotCaptured = "CLASSIFICATION.SNAPSHOT_CAPTURED";

    /// <summary>Stable audit code emitted when at least one drift finding is detected.</summary>
    public const string AuditDriftDetected = "CLASSIFICATION.DRIFT_DETECTED";

    /// <summary>Stable audit code emitted on drift acknowledgement.</summary>
    public const string AuditDriftAcknowledged = "CLASSIFICATION.DRIFT_ACKNOWLEDGED";

    /// <summary>Maximum permitted recent-snapshots <c>take</c>.</summary>
    public const int MaxRecentSnapshotsTake = 100;

    private readonly ICnasDbContext _db;
    private readonly IClassificationCatalogScanner _scanner;
    private readonly IAuditService _audit;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IValidator<ClassificationCatalogEntryFilterDto> _entryFilterValidator;
    private readonly IValidator<ClassificationDriftFilterDto> _driftFilterValidator;
    private readonly IValidator<ClassificationDriftAcknowledgeInputDto> _ackValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer DB context.</param>
    /// <param name="scanner">Reflection scanner over the Contracts assembly.</param>
    /// <param name="audit">Audit service — emits the capture + drift + ack rows.</param>
    /// <param name="sqids">Sqid encoder/decoder for boundary id translation.</param>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md RULE 4).</param>
    /// <param name="caller">Caller context used to attribute manual snapshots + acknowledgements.</param>
    /// <param name="entryFilterValidator">Validator for the snapshot-details filter envelope.</param>
    /// <param name="driftFilterValidator">Validator for the drift-list filter envelope.</param>
    /// <param name="ackValidator">Validator for the acknowledgement payload.</param>
    public ClassificationCatalogService(
        ICnasDbContext db,
        IClassificationCatalogScanner scanner,
        IAuditService audit,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IValidator<ClassificationCatalogEntryFilterDto> entryFilterValidator,
        IValidator<ClassificationDriftFilterDto> driftFilterValidator,
        IValidator<ClassificationDriftAcknowledgeInputDto> ackValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(entryFilterValidator);
        ArgumentNullException.ThrowIfNull(driftFilterValidator);
        ArgumentNullException.ThrowIfNull(ackValidator);
        _db = db;
        _scanner = scanner;
        _audit = audit;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _entryFilterValidator = entryFilterValidator;
        _driftFilterValidator = driftFilterValidator;
        _ackValidator = ackValidator;
    }

    /// <inheritdoc />
    public Task<Result<ClassificationCatalogSnapshotDto>> CaptureManualSnapshotAsync(
        CancellationToken cancellationToken = default)
        => CaptureCoreAsync(ClassificationSnapshotTriggerKind.Manual, _caller.UserSqid ?? "admin", cancellationToken);

    /// <inheritdoc />
    public Task<Result<ClassificationCatalogSnapshotDto>> CaptureScheduledSnapshotAsync(
        CancellationToken cancellationToken = default)
        => CaptureCoreAsync(ClassificationSnapshotTriggerKind.Scheduled, "system", cancellationToken);

    /// <inheritdoc />
    public async Task<Result<ClassificationCatalogSnapshotDto>> GetSnapshotByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ClassificationCatalogSnapshotDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var snapshot = await _db.ClassificationCatalogSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Result<ClassificationCatalogSnapshotDto>.Failure(
                ErrorCodes.NotFound, "Classification snapshot not found.");
        }
        return Result<ClassificationCatalogSnapshotDto>.Success(ToDto(snapshot));
    }

    /// <inheritdoc />
    public async Task<Result<ClassificationCatalogSnapshotDetailsDto>> GetSnapshotDetailsAsync(
        string sqid,
        ClassificationCatalogEntryFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var validation = await _entryFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ClassificationCatalogSnapshotDetailsDto>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString());
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ClassificationCatalogSnapshotDetailsDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var snapshot = await _db.ClassificationCatalogSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Result<ClassificationCatalogSnapshotDetailsDto>.Failure(
                ErrorCodes.NotFound, "Classification snapshot not found.");
        }

        IQueryable<ClassificationCatalogEntry> query = _db.ClassificationCatalogEntries
            .AsNoTracking()
            .Where(e => e.SnapshotId == snapshot.Id && e.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Label))
        {
            var labelName = filter.Label;
            query = query.Where(e => e.Label == labelName);
        }
        if (filter.IsExplicit is { } expFlag)
        {
            query = query.Where(e => e.IsExplicit == expFlag);
        }
        if (!string.IsNullOrWhiteSpace(filter.TypeFullNameContains))
        {
            var needle = filter.TypeFullNameContains;
            query = query.Where(e => e.TypeFullName.Contains(needle));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderBy(e => e.TypeFullName)
            .ThenBy(e => e.PropertyName)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var entries = rows.Select(ToDto).ToList();
        var details = new ClassificationCatalogSnapshotDetailsDto(
            Snapshot: ToDto(snapshot),
            Entries: entries,
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<ClassificationCatalogSnapshotDetailsDto>.Success(details);
    }

    /// <inheritdoc />
    public async Task<Result<ClassificationCatalogSnapshotPageDto>> ListSnapshotsAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take < 1 || take > MaxRecentSnapshotsTake)
        {
            return Result<ClassificationCatalogSnapshotPageDto>.Failure(
                ErrorCodes.ValidationFailed,
                $"take must be in 1..{MaxRecentSnapshotsTake}.");
        }
        var total = await _db.ClassificationCatalogSnapshots
            .AsNoTracking()
            .Where(s => s.IsActive)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await _db.ClassificationCatalogSnapshots
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderByDescending(s => s.CapturedAt)
            .ThenByDescending(s => s.Id)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var page = new ClassificationCatalogSnapshotPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total);
        return Result<ClassificationCatalogSnapshotPageDto>.Success(page);
    }

    /// <inheritdoc />
    public async Task<Result<ClassificationDriftResultDto>> ComputeDriftAsync(
        string baselineSnapshotSqid,
        string currentSnapshotSqid,
        CancellationToken cancellationToken = default)
    {
        var decBaseline = _sqids.TryDecode(baselineSnapshotSqid);
        if (decBaseline.IsFailure)
        {
            return Result<ClassificationDriftResultDto>.Failure(decBaseline.ErrorCode!, decBaseline.ErrorMessage!);
        }
        var decCurrent = _sqids.TryDecode(currentSnapshotSqid);
        if (decCurrent.IsFailure)
        {
            return Result<ClassificationDriftResultDto>.Failure(decCurrent.ErrorCode!, decCurrent.ErrorMessage!);
        }

        var baseline = await _db.ClassificationCatalogSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == decBaseline.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (baseline is null)
        {
            return Result<ClassificationDriftResultDto>.Failure(
                ErrorCodes.NotFound, "Baseline snapshot not found.");
        }
        var current = await _db.ClassificationCatalogSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == decCurrent.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return Result<ClassificationDriftResultDto>.Failure(
                ErrorCodes.NotFound, "Current snapshot not found.");
        }
        if (baseline.Id == current.Id)
        {
            return Result<ClassificationDriftResultDto>.Failure(
                ErrorCodes.ValidationFailed, "Baseline and current snapshots must differ.");
        }

        // Idempotent re-run: if findings already exist for the pair, return them.
        var existing = await _db.ClassificationDriftFindings
            .AsNoTracking()
            .Where(f => f.BaselineSnapshotId == baseline.Id
                        && f.CurrentSnapshotId == current.Id
                        && f.IsActive)
            .OrderBy(f => f.TypeFullName)
            .ThenBy(f => f.PropertyName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return Result<ClassificationDriftResultDto>.Success(
                new ClassificationDriftResultDto(
                    BaselineSnapshotSqid: _sqids.Encode(baseline.Id),
                    CurrentSnapshotSqid: _sqids.Encode(current.Id),
                    FindingsCount: existing.Count,
                    Findings: existing.Select(ToDto).ToList()));
        }

        var baselineEntries = await _db.ClassificationCatalogEntries
            .AsNoTracking()
            .Where(e => e.SnapshotId == baseline.Id && e.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentEntries = await _db.ClassificationCatalogEntries
            .AsNoTracking()
            .Where(e => e.SnapshotId == current.Id && e.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var newFindings = DetectDrift(baselineEntries, currentEntries);
        if (newFindings.Count == 0)
        {
            return Result<ClassificationDriftResultDto>.Success(
                new ClassificationDriftResultDto(
                    BaselineSnapshotSqid: _sqids.Encode(baseline.Id),
                    CurrentSnapshotSqid: _sqids.Encode(current.Id),
                    FindingsCount: 0,
                    Findings: Array.Empty<ClassificationDriftFindingDto>()));
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        foreach (var (kind, type, prop, baselineLabel, currentLabel) in newFindings)
        {
            _db.ClassificationDriftFindings.Add(new ClassificationDriftFinding
            {
                BaselineSnapshotId = baseline.Id,
                CurrentSnapshotId = current.Id,
                DriftKind = kind,
                TypeFullName = type,
                PropertyName = prop,
                BaselineLabel = baselineLabel,
                CurrentLabel = currentLabel,
                Acknowledged = false,
                DetectedAt = now,
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
            });

            CnasMeter.ClassificationDriftDetected.Add(
                1, new KeyValuePair<string, object?>("drift_kind", kind.ToString()));
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // One audit per (baseline, current) pair with the finding count.
        var details = JsonSerializer.Serialize(new
        {
            baselineSnapshotId = baseline.Id,
            currentSnapshotId = current.Id,
            findingsCount = newFindings.Count,
        });
        await _audit.RecordAsync(
            AuditDriftDetected,
            AuditSeverity.Critical,
            actor,
            nameof(ClassificationDriftFinding),
            current.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        var persisted = await _db.ClassificationDriftFindings
            .AsNoTracking()
            .Where(f => f.BaselineSnapshotId == baseline.Id
                        && f.CurrentSnapshotId == current.Id
                        && f.IsActive)
            .OrderBy(f => f.TypeFullName)
            .ThenBy(f => f.PropertyName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Result<ClassificationDriftResultDto>.Success(
            new ClassificationDriftResultDto(
                BaselineSnapshotSqid: _sqids.Encode(baseline.Id),
                CurrentSnapshotSqid: _sqids.Encode(current.Id),
                FindingsCount: persisted.Count,
                Findings: persisted.Select(ToDto).ToList()));
    }

    /// <inheritdoc />
    public async Task<Result<ClassificationDriftPageDto>> ListDriftFindingsAsync(
        ClassificationDriftFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var validation = await _driftFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ClassificationDriftPageDto>.Failure(ErrorCodes.ValidationFailed, validation.ToString());
        }

        IQueryable<ClassificationDriftFinding> query = _db.ClassificationDriftFindings
            .AsNoTracking()
            .Where(f => f.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.DriftKind)
            && Enum.TryParse<ClassificationDriftKind>(filter.DriftKind, ignoreCase: false, out var kind))
        {
            query = query.Where(f => f.DriftKind == kind);
        }
        if (filter.Acknowledged is { } ackFlag)
        {
            query = query.Where(f => f.Acknowledged == ackFlag);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(f => f.DetectedAt)
            .ThenByDescending(f => f.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new ClassificationDriftPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<ClassificationDriftPageDto>.Success(page);
    }

    /// <inheritdoc />
    public async Task<Result<ClassificationDriftFindingDto>> AcknowledgeDriftAsync(
        string findingSqid,
        ClassificationDriftAcknowledgeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _ackValidator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ClassificationDriftFindingDto>.Failure(ErrorCodes.ValidationFailed, validation.ToString());
        }

        var decoded = _sqids.TryDecode(findingSqid);
        if (decoded.IsFailure)
        {
            return Result<ClassificationDriftFindingDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var finding = await _db.ClassificationDriftFindings
            .FirstOrDefaultAsync(f => f.Id == decoded.Value && f.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (finding is null)
        {
            return Result<ClassificationDriftFindingDto>.Failure(
                ErrorCodes.NotFound, "Drift finding not found.");
        }
        if (finding.Acknowledged)
        {
            return Result<ClassificationDriftFindingDto>.Failure(
                ErrorCodes.Conflict, "Finding is already acknowledged.");
        }

        var now = _clock.UtcNow;
        finding.Acknowledged = true;
        finding.AcknowledgedAt = now;
        finding.AcknowledgedByUserId = _caller.UserId;
        finding.AcknowledgementNote = input.Note;
        finding.UpdatedAtUtc = now;
        finding.UpdatedBy = _caller.UserSqid ?? "admin";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.ClassificationDriftAcknowledged.Add(1);

        var details = JsonSerializer.Serialize(new
        {
            findingId = finding.Id,
            driftKind = finding.DriftKind.ToString(),
            typeFullName = finding.TypeFullName,
            propertyName = finding.PropertyName,
        });
        await _audit.RecordAsync(
            AuditDriftAcknowledged,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "admin",
            nameof(ClassificationDriftFinding),
            finding.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<ClassificationDriftFindingDto>.Success(ToDto(finding));
    }

    /// <summary>
    /// Shared capture path. Runs the scanner, persists the snapshot +
    /// entries, emits the metric + audit, and returns the wire DTO.
    /// </summary>
    /// <param name="triggerKind">Origin of the snapshot.</param>
    /// <param name="actor">Audit-attribution identifier.</param>
    /// <param name="cancellationToken">Cancellation propagated from the caller.</param>
    /// <returns>The freshly persisted snapshot.</returns>
    private async Task<Result<ClassificationCatalogSnapshotDto>> CaptureCoreAsync(
        ClassificationSnapshotTriggerKind triggerKind,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var snapshot = new ClassificationCatalogSnapshot
        {
            CapturedAt = now,
            TriggerKind = triggerKind,
            Status = ClassificationSnapshotStatus.Capturing,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.ClassificationCatalogSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var scanResult = await _scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            if (scanResult.IsFailure)
            {
                snapshot.Status = ClassificationSnapshotStatus.Failed;
                snapshot.FailureReason = $"{scanResult.ErrorCode}: {scanResult.ErrorMessage}";
                snapshot.UpdatedAtUtc = _clock.UtcNow;
                snapshot.UpdatedBy = actor;
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return Result<ClassificationCatalogSnapshotDto>.Failure(
                    scanResult.ErrorCode!, scanResult.ErrorMessage!);
            }

            var outcome = scanResult.Value;
            var completionTime = _clock.UtcNow;

            foreach (var property in outcome.Properties)
            {
                _db.ClassificationCatalogEntries.Add(new ClassificationCatalogEntry
                {
                    SnapshotId = snapshot.Id,
                    TypeFullName = property.TypeFullName,
                    PropertyName = property.PropertyName,
                    Label = property.Label,
                    IsExplicit = property.IsExplicit,
                    DeclaringAssembly = property.DeclaringAssembly,
                    Notes = null,
                    CreatedAtUtc = completionTime,
                    CreatedBy = actor,
                    IsActive = true,
                });
            }

            snapshot.Status = ClassificationSnapshotStatus.Captured;
            snapshot.TotalTypesScanned = outcome.TotalTypesScanned;
            snapshot.TotalPropertiesClassified = outcome.TotalPropertiesClassified;
            snapshot.TotalPropertiesUnclassified = outcome.TotalPropertiesUnclassified;
            snapshot.LabelCountsJson = JsonSerializer.Serialize(outcome.LabelCounts);
            snapshot.AssemblyVersionsJson = JsonSerializer.Serialize(outcome.AssemblyVersions);
            snapshot.UpdatedAtUtc = completionTime;
            snapshot.UpdatedBy = actor;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            CnasMeter.ClassificationSnapshotCaptured.Add(
                1, new KeyValuePair<string, object?>("trigger_kind", triggerKind.ToString()));

            var details = JsonSerializer.Serialize(new
            {
                snapshotId = snapshot.Id,
                triggerKind = triggerKind.ToString(),
                totalTypesScanned = snapshot.TotalTypesScanned,
                totalPropertiesClassified = snapshot.TotalPropertiesClassified,
                totalPropertiesUnclassified = snapshot.TotalPropertiesUnclassified,
            });
            await _audit.RecordAsync(
                AuditSnapshotCaptured,
                AuditSeverity.Critical,
                actor,
                nameof(ClassificationCatalogSnapshot),
                snapshot.Id,
                details,
                _caller.SourceIp,
                _caller.CorrelationId,
                cancellationToken).ConfigureAwait(false);

            return Result<ClassificationCatalogSnapshotDto>.Success(ToDto(snapshot));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            snapshot.Status = ClassificationSnapshotStatus.Failed;
            snapshot.FailureReason = ex.GetType().Name + ": " + ex.Message;
            snapshot.UpdatedAtUtc = _clock.UtcNow;
            snapshot.UpdatedBy = actor;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<ClassificationCatalogSnapshotDto>.Failure(
                ErrorCodes.Internal, snapshot.FailureReason);
        }
    }

    /// <summary>
    /// Computes the drift set between two entry collections. Pure function.
    /// </summary>
    /// <param name="baselineEntries">Baseline snapshot's persisted entries.</param>
    /// <param name="currentEntries">Current snapshot's persisted entries.</param>
    /// <returns>Drift tuples (kind, type, property, baselineLabel?, currentLabel?).</returns>
    private static List<(ClassificationDriftKind Kind, string Type, string Prop, string? BaselineLabel, string? CurrentLabel)>
        DetectDrift(IReadOnlyList<ClassificationCatalogEntry> baselineEntries, IReadOnlyList<ClassificationCatalogEntry> currentEntries)
    {
        var findings = new List<(ClassificationDriftKind, string, string, string?, string?)>();
        var baselineMap = baselineEntries
            .ToDictionary(
                e => (e.TypeFullName, e.PropertyName),
                e => e);
        var currentMap = currentEntries
            .ToDictionary(
                e => (e.TypeFullName, e.PropertyName),
                e => e);

        // Added + LabelChanged + ClassificationLost.
        foreach (var kv in currentMap)
        {
            if (!baselineMap.TryGetValue(kv.Key, out var baseline))
            {
                findings.Add((
                    ClassificationDriftKind.Added,
                    kv.Key.TypeFullName,
                    kv.Key.PropertyName,
                    null,
                    kv.Value.Label));
                continue;
            }

            if (!string.Equals(baseline.Label, kv.Value.Label, StringComparison.Ordinal))
            {
                findings.Add((
                    ClassificationDriftKind.LabelChanged,
                    kv.Key.TypeFullName,
                    kv.Key.PropertyName,
                    baseline.Label,
                    kv.Value.Label));
            }

            if (baseline.IsExplicit && !kv.Value.IsExplicit)
            {
                findings.Add((
                    ClassificationDriftKind.ClassificationLost,
                    kv.Key.TypeFullName,
                    kv.Key.PropertyName,
                    baseline.Label,
                    kv.Value.Label));
            }
        }

        // Removed.
        foreach (var kv in baselineMap)
        {
            if (!currentMap.ContainsKey(kv.Key))
            {
                findings.Add((
                    ClassificationDriftKind.Removed,
                    kv.Key.TypeFullName,
                    kv.Key.PropertyName,
                    kv.Value.Label,
                    null));
            }
        }

        // Deterministic ordering.
        findings.Sort(static (a, b) =>
        {
            var t = string.CompareOrdinal(a.Item2, b.Item2);
            if (t != 0) return t;
            var p = string.CompareOrdinal(a.Item3, b.Item3);
            if (p != 0) return p;
            return a.Item1.CompareTo(b.Item1);
        });
        return findings;
    }

    /// <summary>Maps a persisted snapshot to its wire DTO.</summary>
    /// <param name="snapshot">Persisted snapshot.</param>
    /// <returns>Wire DTO.</returns>
    private ClassificationCatalogSnapshotDto ToDto(ClassificationCatalogSnapshot snapshot)
    {
        var labelCounts = ParseStringIntDictionary(snapshot.LabelCountsJson);
        var assemblyVersions = ParseStringStringDictionary(snapshot.AssemblyVersionsJson);
        return new ClassificationCatalogSnapshotDto(
            Id: _sqids.Encode(snapshot.Id),
            CapturedAt: snapshot.CapturedAt,
            TriggerKind: snapshot.TriggerKind.ToString(),
            Status: snapshot.Status.ToString(),
            TotalTypesScanned: snapshot.TotalTypesScanned,
            TotalPropertiesClassified: snapshot.TotalPropertiesClassified,
            TotalPropertiesUnclassified: snapshot.TotalPropertiesUnclassified,
            LabelCounts: labelCounts,
            AssemblyVersions: assemblyVersions,
            FailureReason: snapshot.FailureReason);
    }

    /// <summary>Maps a persisted entry to its wire DTO.</summary>
    /// <param name="entry">Persisted entry.</param>
    /// <returns>Wire DTO.</returns>
    private ClassificationCatalogEntryDto ToDto(ClassificationCatalogEntry entry)
        => new(
            Id: _sqids.Encode(entry.Id),
            SnapshotSqid: _sqids.Encode(entry.SnapshotId),
            TypeFullName: entry.TypeFullName,
            PropertyName: entry.PropertyName,
            Label: entry.Label,
            IsExplicit: entry.IsExplicit,
            DeclaringAssembly: entry.DeclaringAssembly,
            Notes: entry.Notes);

    /// <summary>Maps a persisted drift finding to its wire DTO.</summary>
    /// <param name="finding">Persisted finding.</param>
    /// <returns>Wire DTO.</returns>
    private ClassificationDriftFindingDto ToDto(ClassificationDriftFinding finding)
        => new(
            Id: _sqids.Encode(finding.Id),
            BaselineSnapshotSqid: _sqids.Encode(finding.BaselineSnapshotId),
            CurrentSnapshotSqid: _sqids.Encode(finding.CurrentSnapshotId),
            DriftKind: finding.DriftKind.ToString(),
            TypeFullName: finding.TypeFullName,
            PropertyName: finding.PropertyName,
            BaselineLabel: finding.BaselineLabel,
            CurrentLabel: finding.CurrentLabel,
            Acknowledged: finding.Acknowledged,
            AcknowledgedAt: finding.AcknowledgedAt,
            AcknowledgementNote: finding.AcknowledgementNote,
            DetectedAt: finding.DetectedAt);

    /// <summary>
    /// Parses a stored JSON <c>string→int</c> map back to a typed dictionary,
    /// tolerating null / malformed inputs by returning an empty dictionary.
    /// </summary>
    /// <param name="json">Stored JSON string (or null).</param>
    /// <returns>A dictionary; never null.</returns>
    private static IReadOnlyDictionary<string, int> ParseStringIntDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json)
                   ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Parses a stored JSON <c>string→string</c> map back to a typed dictionary,
    /// tolerating null / malformed inputs by returning an empty dictionary.
    /// </summary>
    /// <param name="json">Stored JSON string (or null).</param>
    /// <returns>A dictionary; never null.</returns>
    private static IReadOnlyDictionary<string, string> ParseStringStringDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
