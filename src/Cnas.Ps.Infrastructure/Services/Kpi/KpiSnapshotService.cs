using System.Diagnostics;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Kpi;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Kpi;

/// <summary>
/// R0201 / TOR CF 20.02 — default implementation of
/// <see cref="IKpiSnapshotService"/>. Orchestrates every registered
/// <see cref="IKpiCalculator"/>, upserts emitted entries against the
/// natural key (date + KPI code + dimensions), and exposes the dashboard
/// read API. See the interface for the full contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Scoped — captures the per-request DbContext for the
/// upsert path. The job composition wires a fresh scope per fire so the
/// service can be invoked from a background <see cref="Quartz.IJob"/>
/// without leaking state.
/// </para>
/// <para>
/// <b>Upsert algorithm.</b> The service queries the existing rows for the
/// requested date once, then walks the calculator output and either updates
/// the matching row in place (latest value wins) or inserts a fresh one.
/// One <see cref="ICnasDbContext.SaveChangesAsync"/> call at the end keeps
/// the entire run atomic; a transient DB outage rolls the whole run back
/// rather than leaving a partial snapshot.
/// </para>
/// <para>
/// <b>Audit contract.</b> Exactly one audit row per run with the stable
/// event code <c>KPI.SNAPSHOT.COMPLETED</c>, severity
/// <see cref="AuditSeverity.Information"/>, and a JSON details payload
/// carrying the run counters. The actor is the literal
/// <c>"system:kpi-snapshot"</c> — no caller identity is captured because the
/// run is system-initiated.
/// </para>
/// </remarks>
public sealed class KpiSnapshotService : IKpiSnapshotService
{
    /// <summary>Stable actor id stamped on every audit row emitted by this service.</summary>
    private const string SystemActor = "system:kpi-snapshot";

    /// <summary>Stable audit event code for a completed snapshot run.</summary>
    private const string AuditEventCode = "KPI.SNAPSHOT.COMPLETED";

    private readonly ICnasDbContext _writeDb;
    private readonly IReadOnlyCnasDbContext _readDb;
    private readonly IEnumerable<IKpiCalculator> _calculators;
    private readonly IAuditService _audit;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ILogger<KpiSnapshotService> _logger;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="writeDb">Primary DbContext used for the upsert path.</param>
    /// <param name="readDb">Read-only context used by the dashboard query paths.</param>
    /// <param name="calculators">DI-registered KPI calculators, one per code.</param>
    /// <param name="audit">Audit facade.</param>
    /// <param name="sqids">Sqid encoder for the surrogate run id.</param>
    /// <param name="clock">UTC clock — supplies <c>UpdatedAtUtc</c> stamps.</param>
    /// <param name="logger">Structured logger.</param>
    public KpiSnapshotService(
        ICnasDbContext writeDb,
        IReadOnlyCnasDbContext readDb,
        IEnumerable<IKpiCalculator> calculators,
        IAuditService audit,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ILogger<KpiSnapshotService> logger)
    {
        ArgumentNullException.ThrowIfNull(writeDb);
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(calculators);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _writeDb = writeDb;
        _readDb = readDb;
        _calculators = calculators;
        _audit = audit;
        _sqids = sqids;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<KpiSnapshotRunDto>> RunForDateAsync(
        DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var now = _clock.UtcNow;

        // Load existing rows once so the per-entry merge does not need a DB
        // round-trip per calculator output line.
        var existing = await _writeDb.KpiSnapshots
            .Where(s => s.SnapshotDate == snapshotDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var existingByKey = existing.ToDictionary(NaturalKey, StringComparer.Ordinal);

        var calculatorList = _calculators.ToList();
        var rowsUpserted = 0;

        foreach (var calculator in calculatorList)
        {
            IReadOnlyList<KpiSnapshotEntry> entries;
            try
            {
                entries = await calculator.ComputeAsync(snapshotDate, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "KpiSnapshotService calculator {KpiCode} threw — skipping for snapshot {Date}.",
                    calculator.KpiCode, snapshotDate);
                continue;
            }

            foreach (var entry in entries)
            {
                // Defensive: the calculator MUST emit its own KPI code.
                if (!string.Equals(entry.KpiCode, calculator.KpiCode, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "KpiSnapshotService calculator {Calc} emitted entry with mismatched KPI code {EntryCode}; ignored.",
                        calculator.KpiCode, entry.KpiCode);
                    continue;
                }

                var key = $"{entry.KpiCode}|{entry.Dimension1}|{entry.Dimension2}";
                if (existingByKey.TryGetValue(key, out var row))
                {
                    // Upsert: overwrite the value + unit, refresh the audit stamp.
                    row.Value = entry.Value;
                    row.ValueUnit = entry.ValueUnit;
                    row.UpdatedAtUtc = now;
                    row.UpdatedBy = SystemActor;
                }
                else
                {
                    row = new KpiSnapshot
                    {
                        CreatedAtUtc = now,
                        CreatedBy = SystemActor,
                        SnapshotDate = snapshotDate,
                        KpiCode = entry.KpiCode,
                        Value = entry.Value,
                        ValueUnit = entry.ValueUnit,
                        Dimension1 = entry.Dimension1 ?? string.Empty,
                        Dimension2 = entry.Dimension2 ?? string.Empty,
                        IsActive = true,
                    };
                    _writeDb.KpiSnapshots.Add(row);
                    existingByKey[key] = row;
                }
                rowsUpserted += 1;
            }
        }

        await _writeDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        sw.Stop();
        var durationMs = sw.ElapsedMilliseconds;

        var detailsJson = JsonSerializer.Serialize(new
        {
            snapshotDate = snapshotDate.ToString("yyyy-MM-dd"),
            calculatorsRun = calculatorList.Count,
            rowsUpserted,
            durationMs,
        });

        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Information,
            actorId: SystemActor,
            targetEntity: nameof(KpiSnapshot),
            targetEntityId: null,
            detailsJson: detailsJson,
            sourceIp: null,
            correlationId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "KpiSnapshotService run for {Date} completed: calculators={Calc} rows={Rows} duration={DurationMs}ms",
            snapshotDate, calculatorList.Count, rowsUpserted, durationMs);

        // Deterministic surrogate id derived from the snapshot date's
        // numeric form (yyyymmdd). The Sqid encoding is reversible, so an
        // operator can trace a run-id back to the date via TryDecode.
        var surrogateId = ComputeSurrogateId(snapshotDate);
        var sqid = _sqids.Encode(surrogateId);
        return Result<KpiSnapshotRunDto>.Success(new KpiSnapshotRunDto(
            Id: sqid,
            SnapshotDate: snapshotDate,
            CalculatorsRun: calculatorList.Count,
            RowsUpserted: rowsUpserted,
            DurationMs: durationMs));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KpiSnapshotDto>> QueryAsync(
        DateOnly fromDate, DateOnly toDate, string? kpiCodeFilter,
        CancellationToken cancellationToken = default)
    {
        var query = _readDb.KpiSnapshots
            .Where(s => s.SnapshotDate >= fromDate && s.SnapshotDate <= toDate);
        if (!string.IsNullOrWhiteSpace(kpiCodeFilter))
        {
            query = query.Where(s => s.KpiCode == kpiCodeFilter);
        }

        var rows = await query
            .OrderByDescending(s => s.SnapshotDate)
            .ThenBy(s => s.KpiCode)
            .ThenBy(s => s.Dimension1)
            .ThenBy(s => s.Dimension2)
            .Select(s => new KpiSnapshotDto(
                s.SnapshotDate, s.KpiCode, s.Value, s.ValueUnit, s.Dimension1, s.Dimension2))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, decimal>> GetLatestAsync(
        IReadOnlyCollection<string> kpiCodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kpiCodes);
        var codes = kpiCodes.Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count == 0)
        {
            return new Dictionary<string, decimal>(StringComparer.Ordinal);
        }

        // For each code, find the highest SnapshotDate that has data and sum
        // across dimensions on that date. Two round-trips per call is fine —
        // this is a small read invoked by the dashboard tiles.
        var matching = await _readDb.KpiSnapshots
            .Where(s => codes.Contains(s.KpiCode))
            .Select(s => new { s.KpiCode, s.SnapshotDate, s.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var grouping in matching.GroupBy(r => r.KpiCode, StringComparer.Ordinal))
        {
            var latestDate = grouping.Max(r => r.SnapshotDate);
            var sum = grouping.Where(r => r.SnapshotDate == latestDate).Sum(r => r.Value);
            result[grouping.Key] = sum;
        }
        return result;
    }

    /// <summary>
    /// Builds the natural-key compound string for in-memory upsert lookup.
    /// Mirrors the (KpiCode, Dimension1, Dimension2) tuple of the unique
    /// index — the snapshot date is implicit because the calling code only
    /// loaded rows for a single date.
    /// </summary>
    /// <param name="row">Source snapshot row.</param>
    /// <returns>The compound natural key.</returns>
    private static string NaturalKey(KpiSnapshot row) =>
        $"{row.KpiCode}|{row.Dimension1}|{row.Dimension2}";

    /// <summary>
    /// Computes the deterministic surrogate run id from the snapshot date —
    /// <c>yyyy * 10000 + mm * 100 + dd</c>. The value is non-negative and
    /// fits comfortably in <see cref="long"/>; the Sqid encoder accepts it
    /// unchanged.
    /// </summary>
    /// <param name="date">The snapshot date.</param>
    /// <returns>The surrogate id used as the Sqid input.</returns>
    private static long ComputeSurrogateId(DateOnly date) =>
        (date.Year * 10000L) + (date.Month * 100L) + date.Day;
}
