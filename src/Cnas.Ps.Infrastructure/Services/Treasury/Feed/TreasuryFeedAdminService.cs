using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — production implementation of
/// <see cref="ITreasuryFeedAdminService"/>. Hosts the manual-trigger entry,
/// per-import lookups, the rows drill-down, and the imports list page.
/// </summary>
public sealed class TreasuryFeedAdminService : ITreasuryFeedAdminService
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
    private readonly ITreasuryFeedImporter _importer;
    private readonly IValidator<TreasuryFeedImportFilterDto> _filterValidator;
    private readonly IValidator<TreasuryFeedImportRowFilterDto> _rowFilterValidator;

    /// <summary>Constructs the service with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context (used solely to defer to the importer + read the registry rows).</param>
    /// <param name="read">Read-replica context — list + lookups go here.</param>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md RULE 4).</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="importer">The importer this service delegates to for manual runs.</param>
    /// <param name="filterValidator">Validator for the imports list filter envelope.</param>
    /// <param name="rowFilterValidator">Validator for the rows-details filter envelope.</param>
    public TreasuryFeedAdminService(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        ITreasuryFeedImporter importer,
        IValidator<TreasuryFeedImportFilterDto> filterValidator,
        IValidator<TreasuryFeedImportRowFilterDto> rowFilterValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(filterValidator);
        ArgumentNullException.ThrowIfNull(rowFilterValidator);
        _db = db;
        _read = read;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _importer = importer;
        _filterValidator = filterValidator;
        _rowFilterValidator = rowFilterValidator;
    }

    /// <inheritdoc />
    public async Task<Result<TreasuryFeedImportSummaryDto>> TriggerManualImportAsync(
        DateOnly feedDate,
        CancellationToken cancellationToken = default)
    {
        // Validate the manual-input envelope against the live clock today.
        var violation = TreasuryFeedManualImportInputValidator.Validate(feedDate, _clock.TodayUtc);
        if (violation is not null)
        {
            return Result<TreasuryFeedImportSummaryDto>.Failure(ErrorCodes.ValidationFailed, violation);
        }

        var actor = _caller.UserSqid ?? "admin";
        var details = JsonSerializer.Serialize(new
        {
            feedDate = feedDate.ToString("O", CultureInfo.InvariantCulture),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            ITreasuryFeedAdminService.AuditManualImportStarted,
            AuditSeverity.Critical,
            actor,
            nameof(TreasuryFeedImport),
            null,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return await _importer.ImportAsync(feedDate, TreasuryFeedTriggerKind.Manual, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<TreasuryFeedImportDto>> GetImportByIdAsync(
        string sqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<TreasuryFeedImportDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _read.TreasuryFeedImports
            .FirstOrDefaultAsync(i => i.Id == decoded.Value && i.IsActive, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<TreasuryFeedImportDto>.Failure(ErrorCodes.NotFound, "Treasury feed import not found.")
            : Result<TreasuryFeedImportDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<TreasuryFeedImportDetailsDto>> GetImportDetailsAsync(
        string sqid,
        TreasuryFeedImportRowFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _rowFilterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<TreasuryFeedImportDetailsDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<TreasuryFeedImportDetailsDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _read.TreasuryFeedImports
            .FirstOrDefaultAsync(i => i.Id == decoded.Value && i.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<TreasuryFeedImportDetailsDto>.Failure(
                ErrorCodes.NotFound, "Treasury feed import not found.");
        }

        IQueryable<TreasuryFeedImportRow> rowsQuery = _read.TreasuryFeedImportRows
            .Where(r => r.ImportId == decoded.Value && r.IsActive);
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<TreasuryFeedImportRowStatus>(filter.Status, ignoreCase: false, out var status))
        {
            rowsQuery = rowsQuery.Where(r => r.Status == status);
        }
        var total = await rowsQuery.CountAsync(cancellationToken).ConfigureAwait(false);
        var rowEntities = await rowsQuery
            .OrderBy(r => r.RowOrdinal)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var page = new TreasuryFeedImportRowPageDto(
            Items: rowEntities.Select(ToRowDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        var dto = new TreasuryFeedImportDetailsDto(ToDto(row), page);
        return Result<TreasuryFeedImportDetailsDto>.Success(dto);
    }

    /// <inheritdoc />
    public async Task<Result<TreasuryFeedImportPageDto>> ListAsync(
        TreasuryFeedImportFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var v = await _filterValidator.ValidateAsync(filter, cancellationToken).ConfigureAwait(false);
        if (!v.IsValid)
        {
            return Result<TreasuryFeedImportPageDto>.Failure(
                ErrorCodes.ValidationFailed, v.Errors[0].ErrorMessage);
        }

        IQueryable<TreasuryFeedImport> query = _read.TreasuryFeedImports.Where(i => i.IsActive);
        if (!string.IsNullOrWhiteSpace(filter.Status)
            && Enum.TryParse<TreasuryFeedImportStatus>(filter.Status, ignoreCase: false, out var status))
        {
            query = query.Where(i => i.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.TriggerKind)
            && Enum.TryParse<TreasuryFeedTriggerKind>(filter.TriggerKind, ignoreCase: false, out var trigger))
        {
            query = query.Where(i => i.TriggerKind == trigger);
        }
        if (filter.FeedDateFrom.HasValue)
        {
            query = query.Where(i => i.FeedDate >= filter.FeedDateFrom.Value);
        }
        if (filter.FeedDateTo.HasValue)
        {
            query = query.Where(i => i.FeedDate <= filter.FeedDateTo.Value);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await query
            .OrderByDescending(i => i.StartedAt)
            .ThenByDescending(i => i.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Defensive: in-memory provider does not honour the writer's SaveChanges
        // unless the caller passed the writer context. The reader path is the
        // production shape — tests using the InMemory provider have their own
        // pre-seeded fixtures.
        _ = _db;

        var page = new TreasuryFeedImportPageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: filter.Skip,
            Take: filter.Take);
        return Result<TreasuryFeedImportPageDto>.Success(page);
    }

    /// <summary>Projects an entity into its full outbound DTO.</summary>
    /// <param name="r">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private TreasuryFeedImportDto ToDto(TreasuryFeedImport r) => new(
        Id: _sqids.Encode(r.Id),
        FeedDate: r.FeedDate,
        Status: r.Status.ToString(),
        SourceKind: r.SourceKind.ToString(),
        SourceReference: r.SourceReference,
        FileSizeBytes: r.FileSizeBytes,
        FileHashSha256: r.FileHashSha256,
        RowsTotal: r.RowsTotal,
        RowsImported: r.RowsImported,
        RowsUpdated: r.RowsUpdated,
        RowsSkipped: r.RowsSkipped,
        RowsFailed: r.RowsFailed,
        StartedAt: r.StartedAt,
        CompletedAt: r.CompletedAt,
        FailureReason: r.FailureReason,
        TriggerKind: r.TriggerKind.ToString());

    /// <summary>Projects a row entity into its outbound DTO.</summary>
    /// <param name="r">Loaded row entity.</param>
    /// <returns>Populated row DTO.</returns>
    private TreasuryFeedImportRowDto ToRowDto(TreasuryFeedImportRow r) => new(
        Id: _sqids.Encode(r.Id),
        RowOrdinal: r.RowOrdinal,
        Status: r.Status.ToString(),
        MappedReceiptId: r.MappedReceiptId,
        ErrorCode: r.ErrorCode,
        ErrorDescription: r.ErrorDescription,
        ProcessedAt: r.ProcessedAt);
}
