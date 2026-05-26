using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R1900-R1905 / iter-145 — production implementation of
/// <see cref="IReportCatalogSeedService"/>. Walks the static
/// <see cref="ReportCatalogDescriptors"/> table once and upserts every row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency.</b> The service is registered as <c>Scoped</c> alongside
/// the writable <see cref="ICnasDbContext"/>. Concurrent refreshes are
/// disjoint per-request — EF's optimistic concurrency on <c>xmin</c> keeps
/// double-writes safe (the second one will rebase or re-fetch).
/// </para>
/// <para>
/// <b>Sqids.</b> Listed catalog rows expose Sqid-encoded ids per CLAUDE.md
/// RULE 3. Internal IDs never leave the service.
/// </para>
/// </remarks>
public sealed class ReportCatalogSeedService : IReportCatalogSeedService
{
    /// <summary>Cached serializer options used when emitting the audit detail JSON.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context (for the upsert path).</param>
    /// <param name="read">Read-replica context (for the listing path — R1904).</param>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md cross-cutting principles).</param>
    /// <param name="sqids">Sqid encoder / decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    public ReportCatalogSeedService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<ReportCatalogRefreshResultDto>> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        // Materialise once so we can build a code → entity lookup without N round-trips.
        var descriptors = ReportCatalogDescriptors.All;
        var existing = await _db.Reports
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingByCode = existing.ToDictionary(r => r.Code, StringComparer.Ordinal);

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system";
        var inserted = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var d in descriptors)
        {
            if (!existingByCode.TryGetValue(d.Code, out var row))
            {
                // INSERT path — create a fresh Report row.
                row = new Report
                {
                    Code = d.Code,
                    DisplayName = d.NameRo,
                    QueryTemplate = d.Code, // dispatcher uses the code itself; recipe lives in code.
                    ParameterSchemaJson = d.ParametersJson,
                    DefaultFormat = d.DefaultFormat,
                    IsPublic = false,
                    NameRo = d.NameRo,
                    Purpose = d.Purpose,
                    Audience = d.Audience,
                    Frequency = d.Frequency,
                    ColumnsJson = d.ColumnsJson,
                    RbacRole = d.RbacRole,
                    Schedule = d.Schedule,
                    OutputFormatsJson = d.OutputFormatsJson,
                    Category = d.Category,
                    CreatedAtUtc = now,
                    CreatedBy = actor,
                    IsActive = true,
                };
                _db.Reports.Add(row);
                inserted++;
                continue;
            }

            // UPSERT path — drift-check then update fields in place when needed.
            var drift =
                !string.Equals(row.DisplayName, d.NameRo, StringComparison.Ordinal) ||
                !string.Equals(row.ParameterSchemaJson, d.ParametersJson, StringComparison.Ordinal) ||
                !string.Equals(row.DefaultFormat, d.DefaultFormat, StringComparison.Ordinal) ||
                !string.Equals(row.NameRo, d.NameRo, StringComparison.Ordinal) ||
                !string.Equals(row.Purpose, d.Purpose, StringComparison.Ordinal) ||
                !string.Equals(row.Audience, d.Audience, StringComparison.Ordinal) ||
                !string.Equals(row.Frequency, d.Frequency, StringComparison.Ordinal) ||
                !string.Equals(row.ColumnsJson, d.ColumnsJson, StringComparison.Ordinal) ||
                !string.Equals(row.RbacRole, d.RbacRole, StringComparison.Ordinal) ||
                !string.Equals(row.Schedule, d.Schedule, StringComparison.Ordinal) ||
                !string.Equals(row.OutputFormatsJson, d.OutputFormatsJson, StringComparison.Ordinal) ||
                !string.Equals(row.Category, d.Category, StringComparison.Ordinal);

            if (!drift)
            {
                unchanged++;
                continue;
            }

            row.DisplayName = d.NameRo;
            row.ParameterSchemaJson = d.ParametersJson;
            row.DefaultFormat = d.DefaultFormat;
            row.NameRo = d.NameRo;
            row.Purpose = d.Purpose;
            row.Audience = d.Audience;
            row.Frequency = d.Frequency;
            row.ColumnsJson = d.ColumnsJson;
            row.RbacRole = d.RbacRole;
            row.Schedule = d.Schedule;
            row.OutputFormatsJson = d.OutputFormatsJson;
            row.Category = d.Category;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = actor;
            updated++;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var result = new ReportCatalogRefreshResultDto(
            Inserted: inserted,
            Updated: updated,
            Unchanged: unchanged,
            Total: descriptors.Count);

        var detail = JsonSerializer.Serialize(result, JsonOptions);
        await _audit.RecordAsync(
            eventCode: IReportCatalogSeedService.AuditCatalogRefreshed,
            severity: AuditSeverity.Critical,
            actorId: actor,
            targetEntity: nameof(Report),
            targetEntityId: null,
            detailsJson: detail,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<ReportCatalogRefreshResultDto>.Success(result);
    }

    /// <inheritdoc />
    public async Task<Result<ReportCatalogPageDto>> ListAsync(
        string? category = null,
        string? frequency = null,
        CancellationToken cancellationToken = default)
    {
        var query = _read.Reports.AsQueryable();
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(r => r.Category == category);
        }
        if (!string.IsNullOrWhiteSpace(frequency))
        {
            query = query.Where(r => r.Frequency == frequency);
        }

        var rows = await query
            .OrderBy(r => r.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows
            .Select(r => new ReportCatalogRowDto(
                Id: _sqids.Encode(r.Id),
                Code: r.Code,
                NameRo: r.NameRo,
                Purpose: r.Purpose,
                Audience: r.Audience,
                Frequency: r.Frequency,
                ParametersJson: r.ParameterSchemaJson,
                ColumnsJson: r.ColumnsJson,
                RbacRole: r.RbacRole,
                Schedule: r.Schedule,
                OutputFormatsJson: r.OutputFormatsJson,
                Category: r.Category,
                DefaultFormat: r.DefaultFormat,
                IsPublic: r.IsPublic))
            .ToList()
            .AsReadOnly();

        // Suppress IDE0079: the format provider is invariant — ordering uses ordinal comparison above.
        _ = CultureInfo.InvariantCulture;

        return Result<ReportCatalogPageDto>.Success(new ReportCatalogPageDto(items, items.Count));
    }
}
