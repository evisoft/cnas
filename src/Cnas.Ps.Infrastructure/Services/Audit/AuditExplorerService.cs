using System.Globalization;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.Exports;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Audit;

/// <summary>
/// R0193 / TOR SEC 052 — production <see cref="IAuditExplorerService"/>
/// implementation. Wires the audit-log table to the canonical QBE primitive
/// (R0163), the query-budget guard (R0167), the universal grid exporter
/// (R0226), and the audit archive (R0188) so the admin explorer surface can
/// search, export, and re-attach audit batches without duplicating the rest of
/// the audit pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped lifetime.</b> Mirrors the other list-style services
/// (<see cref="Cnas.Ps.Infrastructure.Services.SolicitantService"/>) so the
/// per-instance <see cref="LastBudgetVerdict"/> slot stays request-isolated.
/// </para>
/// <para>
/// <b>Why read through <see cref="IReadOnlyCnasDbContext"/>.</b> R0026 routes
/// list-style reads at the streaming replica. The explorer is a pure-read
/// surface; only <see cref="ImportArchiveAsync"/> mutates state, and it uses
/// the write-side <see cref="ICnasDbContext"/> to chain new rows.
/// </para>
/// </remarks>
public sealed class AuditExplorerService : IAuditExplorerService
{
    /// <summary>Read-only DB context routed at the streaming replica (R0026).</summary>
    private readonly IReadOnlyCnasDbContext _readDb;

    /// <summary>Write-side DB context — used only by <see cref="ImportArchiveAsync"/>.</summary>
    private readonly ICnasDbContext _writeDb;

    /// <summary>QBE → LINQ converter (R0163).</summary>
    private readonly IQbeToLinqConverter _qbeConverter;

    /// <summary>Query-budget guard (R0167).</summary>
    private readonly IQueryBudgetService _budget;

    /// <summary>Universal grid exporter façade (R0226).</summary>
    private readonly IGridExporter _exporter;

    /// <summary>Audit archive — replay source for <see cref="ImportArchiveAsync"/> (R0188).</summary>
    private readonly IAuditArchive _archive;

    /// <summary>Audit facade for the <c>AUDIT.ARCHIVE.IMPORTED</c> Critical event.</summary>
    private readonly IAuditService _auditService;

    /// <summary>Caller context — used to record the actor on the import audit row.</summary>
    private readonly ICallerContext _caller;

    /// <summary>Sqid encoder used for the row id and actor / resource id encoding.</summary>
    private readonly ISqidService _sqids;

    /// <summary>Structured logger.</summary>
    private readonly ILogger<AuditExplorerService> _logger;

    /// <summary>Constructs the explorer service with its dependencies.</summary>
    /// <param name="readDb">Read-only DB context (R0026 replica).</param>
    /// <param name="writeDb">Write-side DB context for the import path.</param>
    /// <param name="qbeConverter">QBE → LINQ converter.</param>
    /// <param name="budget">Query-budget guard.</param>
    /// <param name="exporter">Universal grid exporter façade.</param>
    /// <param name="archive">Audit archive — replay source.</param>
    /// <param name="auditService">Audit facade for the Critical import audit row.</param>
    /// <param name="caller">Caller context.</param>
    /// <param name="sqids">Sqid encoder.</param>
    /// <param name="logger">Structured logger.</param>
    public AuditExplorerService(
        IReadOnlyCnasDbContext readDb,
        ICnasDbContext writeDb,
        IQbeToLinqConverter qbeConverter,
        IQueryBudgetService budget,
        IGridExporter exporter,
        IAuditArchive archive,
        IAuditService auditService,
        ICallerContext caller,
        ISqidService sqids,
        ILogger<AuditExplorerService> logger)
    {
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(writeDb);
        ArgumentNullException.ThrowIfNull(qbeConverter);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(logger);

        _readDb = readDb;
        _writeDb = writeDb;
        _qbeConverter = qbeConverter;
        _budget = budget;
        _exporter = exporter;
        _archive = archive;
        _auditService = auditService;
        _caller = caller;
        _sqids = sqids;
        _logger = logger;
    }

    /// <summary>Stable grid name carried on the export metric tag and file prefix.</summary>
    internal const string AuditGridName = "AuditLogs";

    /// <summary>
    /// Hex prefix length surfaced in <see cref="AuditLogRowDto.PrevHashHex"/> /
    /// <see cref="AuditLogRowDto.RowHashHex"/>. The full digest is 64 hex chars;
    /// the prefix is enough to spot a chain break at a glance without leaking
    /// the entire digest.
    /// </summary>
    internal const int HashPrefixLength = 8;

    /// <inheritdoc />
    public QueryBudgetVerdict? LastBudgetVerdict { get; private set; }

    /// <inheritdoc />
    public async Task<Result<AuditLogPageDto>> SearchAsync(
        AuditLogSearchInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        LastBudgetVerdict = null;

        // 1. Server-side validation. The validator caps Take and enforces the
        //    date-range monotonicity invariant — the controller can also run
        //    it before opening the DB scope but defence-in-depth here keeps the
        //    service callable from background flows.
        var validator = new AuditLogSearchInputValidator();
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            return Result<AuditLogPageDto>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        // 2. Build the filtered queryable. The R0167 budget guard counts rows
        //    against this queryable BEFORE materialisation; the QBE predicate
        //    + the date-range bounds become entries in the QueryFilterContext
        //    so the registry's hint rules see the shape of filtering applied.
        var (query, ctx, qbeFailure) = BuildFilteredQuery(input);
        if (qbeFailure is { } failure)
        {
            return Result<AuditLogPageDto>.Failure(failure.ErrorCode!, failure.ErrorMessage!);
        }

        // 3. Budget gate. The AuditLog registry carries a tighter 1000-row
        //    budget than the other registries because audit rows are heavier
        //    and operators browse at scale via the SIEM — the explorer is for
        //    targeted spelunking, not bulk dumps.
        var verdict = await _budget.EvaluateAsync(
            QueryBudgetRegistries.AuditLog,
            query,
            ctx,
            cancellationToken).ConfigureAwait(false);
        LastBudgetVerdict = verdict;

        if (!verdict.Allowed)
        {
            return Result<AuditLogPageDto>.Failure(
                ErrorCodes.QueryTooBroad,
                QueryBudgetFailureEnvelope.FailureMessage);
        }

        // 4. Apply the paging window — Take is already capped by the
        //    validator so the materialised list is bounded.
        var skip = Math.Max(0, input.Skip);
        var take = Math.Clamp(input.Take, 1, AuditLogSearchInputValidator.MaxTake);

        var rows = await query
            .OrderByDescending(a => a.EventAtUtc)
            .ThenByDescending(a => a.Id)
            .Skip(skip)
            .Take(take)
            .Select(a => new AuditLogProjection
            {
                Id = a.Id,
                EventAtUtc = a.EventAtUtc,
                EventCode = a.EventCode,
                Severity = a.Severity,
                ActorId = a.ActorId,
                TargetEntity = a.TargetEntity,
                TargetEntityId = a.TargetEntityId,
                DetailsJson = a.DetailsJson,
                PrevHash = a.PrevHash,
                RowHash = a.RowHash,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = rows.Select(MapRow).ToList();
        var suggestions = take == AuditLogSearchInputValidator.MaxTake && verdict.EstimatedRowCount > take
            ? new[]
                {
                    string.Create(CultureInfo.InvariantCulture,
                        $"Result truncated at the server-side cap of {take} rows; narrow your filter to see more.")
                }
            : Array.Empty<string>();

        return Result<AuditLogPageDto>.Success(new AuditLogPageDto(
            items,
            verdict.EstimatedRowCount,
            suggestions));
    }

    /// <inheritdoc />
    public async Task<Result<GridExportResult>> ExportAsync(
        AuditLogSearchInput input,
        ExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        LastBudgetVerdict = null;

        // 1. Validate — duplicates SearchAsync's gate so a malformed envelope
        //    fails fast before the budget guard runs a count.
        var validator = new AuditLogSearchInputValidator();
        var validation = validator.Validate(input);
        if (!validation.IsValid)
        {
            return Result<GridExportResult>.Failure(
                ErrorCodes.ValidationFailed,
                string.Join("; ", validation.Errors.Select(e => e.ErrorMessage)));
        }

        // 2. Build + budget-guard the queryable. Identical pipeline to SearchAsync.
        var (query, ctx, qbeFailure) = BuildFilteredQuery(input);
        if (qbeFailure is { } failure)
        {
            return Result<GridExportResult>.Failure(failure.ErrorCode!, failure.ErrorMessage!);
        }

        var verdict = await _budget.EvaluateAsync(
            QueryBudgetRegistries.AuditLog,
            query,
            ctx,
            cancellationToken).ConfigureAwait(false);
        LastBudgetVerdict = verdict;

        if (!verdict.Allowed)
        {
            return Result<GridExportResult>.Failure(
                ErrorCodes.QueryTooBroad,
                QueryBudgetFailureEnvelope.FailureMessage);
        }

        // 3. Materialise — ordered DESC by EventAtUtc, matching the explorer
        //    grid's default ordering. The budget cap (1000 rows for AuditLog)
        //    is well below the GridExporter row cap (50 000) so we do not need
        //    a second cap here.
        var rows = await query
            .OrderByDescending(a => a.EventAtUtc)
            .ThenByDescending(a => a.Id)
            .Select(a => new AuditLogProjection
            {
                Id = a.Id,
                EventAtUtc = a.EventAtUtc,
                EventCode = a.EventCode,
                Severity = a.Severity,
                ActorId = a.ActorId,
                TargetEntity = a.TargetEntity,
                TargetEntityId = a.TargetEntityId,
                DetailsJson = a.DetailsJson,
                PrevHash = a.PrevHash,
                RowHash = a.RowHash,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var gridRows = rows.Select(MapToGridRow).ToList();
        var columns = BuildAuditGridColumns();
        var request = new GridExportRequest(
            GridName: AuditGridName,
            Columns: columns,
            Rows: gridRows,
            Title: AuditGridName,
            FooterNote: null,
            Language: "ro");

        return await _exporter.ExportAsync(request, format, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<AuditArchiveImportSummaryDto>> ImportArchiveAsync(
        string archiveKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveKey);

        // 1. Load the archive contents. An empty list signals either a
        //    concurrent delete or a quarantined-malformed payload — surface
        //    that as 404 NotFound so the admin operator gets a clear "nothing
        //    to import" signal rather than a misleading "0 imported" success.
        var records = await _archive.ReadAsync(archiveKey, cancellationToken).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return Result<AuditArchiveImportSummaryDto>.Failure(
                ErrorCodes.NotFound,
                $"Audit archive '{archiveKey}' is missing or empty.");
        }

        var ordered = records.OrderBy(r => r.EventAtUtc).ToList();
        var firstUtc = ordered.First().EventAtUtc;
        var lastUtc = ordered.Last().EventAtUtc;

        // 2. Determine which rows are already on disk by the natural composite
        //    key (EventAtUtc, EventCode, ActorId, TargetEntityId). This is the
        //    idempotency primitive: re-importing the same archive becomes a
        //    no-op rather than a tamper-detect failure on the hash chain.
        var fromUtc = firstUtc;
        var toUtc = lastUtc;
        var candidateKeys = ordered
            .Select(r => new { r.EventAtUtc, r.EventCode, r.ActorId, r.TargetEntityId })
            .ToHashSet();
        var existing = await _writeDb.AuditLogs
            .Where(a => a.EventAtUtc >= fromUtc && a.EventAtUtc <= toUtc)
            .Select(a => new { a.EventAtUtc, a.EventCode, a.ActorId, a.TargetEntityId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingSet = existing.ToHashSet();

        var toInsert = ordered
            .Where(r => !existingSet.Contains(new { r.EventAtUtc, r.EventCode, r.ActorId, r.TargetEntityId }))
            .ToList();

        // 3. Chain + persist the new rows. Identical pipeline to
        //    AuditDrainer.FlushAsync — the projector is the single source of
        //    truth for the canonical-form recipe, so importer + drainer
        //    cannot drift.
        var rowsImported = 0;
        var rowsSkipped = ordered.Count - toInsert.Count;
        if (toInsert.Count > 0)
        {
            try
            {
                var prev = await _writeDb.AuditLogs
                    .OrderByDescending(a => a.Id)
                    .Select(a => a.RowHash)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false)
                    ?? "GENESIS";

                var newRows = new List<AuditLog>(toInsert.Count);
                foreach (var r in toInsert)
                {
                    var rowHash = AuditFlushProjector.ComputeRowHash(r, prev);
                    var row = AuditFlushProjector.ToAuditLog(r);
                    row.PrevHash = prev;
                    row.RowHash = rowHash;
                    newRows.Add(row);
                    prev = rowHash;
                }
                _writeDb.AuditLogs.AddRange(newRows);
                await _writeDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                rowsImported = newRows.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Audit archive import failed during chain+persist for archive {ArchiveKey}.",
                    archiveKey);
                return Result<AuditArchiveImportSummaryDto>.Failure(
                    ErrorCodes.Internal,
                    $"Audit archive import failed: {ex.Message}");
            }
        }

        // 4. Audit the import itself at Critical severity so an operator's
        //    deliberate manual replay is itself traceable. Counts only —
        //    no per-row PII in the details payload (the archive contents are
        //    already on-disk in their PII-redacted shape; we do not echo them).
        var actor = _caller.UserSqid
            ?? (_caller.UserId is { } uid ? _sqids.Encode(uid) : "system");
        var detailsJson = string.Create(CultureInfo.InvariantCulture,
            $"{{\"archiveKey\":\"{EscapeJson(archiveKey)}\",\"imported\":{rowsImported},\"skipped\":{rowsSkipped}}}");

        var auditResult = await _auditService.RecordAsync(
            eventCode: AuditArchiveImportedEventCode,
            severity: AuditSeverity.Critical,
            actorId: actor,
            targetEntity: "AuditLog",
            targetEntityId: null,
            detailsJson: detailsJson,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (auditResult.IsFailure)
        {
            // The audit row is best-effort — if the queue is full we log but
            // do not fail the import; the operator already received the result
            // signal of the import outcome via the response DTO.
            _logger.LogWarning(
                "Failed to record AUDIT.ARCHIVE.IMPORTED for archive {ArchiveKey}: {Code} {Message}",
                archiveKey, auditResult.ErrorCode, auditResult.ErrorMessage);
        }

        return Result<AuditArchiveImportSummaryDto>.Success(new AuditArchiveImportSummaryDto(
            RowsImported: rowsImported,
            RowsSkipped: rowsSkipped,
            FirstUtc: firstUtc,
            LastUtc: lastUtc,
            ArchiveKey: archiveKey));
    }

    /// <summary>
    /// Stable event code recorded by <see cref="ImportArchiveAsync"/> after a
    /// successful (or partial) replay. Critical severity per the AuditDrainer
    /// pattern for any operator-driven mutation of the audit table.
    /// </summary>
    internal const string AuditArchiveImportedEventCode = "AUDIT.ARCHIVE.IMPORTED";

    /// <summary>
    /// Builds the filtered <see cref="IQueryable{AuditLog}"/> + the matching
    /// <see cref="IQueryFilterContext"/> for budget hints. Splits the QBE
    /// envelope from the date-range bounds so the converter handles its own
    /// validation and the bounds become explicit context entries the static
    /// budget policy can inspect.
    /// </summary>
    /// <param name="input">Search envelope.</param>
    /// <returns>
    /// A tuple carrying the filtered queryable, the matching context, and
    /// either <c>null</c> on success or a <see cref="Result"/> failure
    /// carrying the QBE converter's stable error code.
    /// </returns>
    private (IQueryable<AuditLog> Query, IQueryFilterContext Ctx, Result? Failure) BuildFilteredQuery(
        AuditLogSearchInput input)
    {
        IQueryable<AuditLog> query = _readDb.AuditLogs;
        var ctx = new QueryFilterContext();

        // QBE first — failed convert short-circuits before we touch the date
        // bounds so the controller can render a field-targeted ProblemDetails.
        if (input.Filter is { Conditions.Count: > 0 } dto)
        {
            var qbe = MapQbe(dto);
            var converted = _qbeConverter.Convert<AuditLog>(QueryBudgetRegistries.AuditLog, qbe);
            if (converted.IsFailure)
            {
                return (query, ctx, Result.Failure(converted.ErrorCode!, converted.ErrorMessage!));
            }
            query = query.Where(converted.Value);
            // Synthetic context entry — mirrors SolicitantService so the hint
            // rules can recognise that QBE narrowed the query and skip the
            // "you must add EventCode/ActorUserId" hint.
            ctx = ctx.With("Qbe", dto.Conditions.Count.ToString(CultureInfo.InvariantCulture));
            // If the QBE envelope filtered on EventCode or ActorId, hoist that
            // into the context so the policy's targeted hint suppression
            // matches the named field rather than relying on the catch-all.
            foreach (var cond in dto.Conditions)
            {
                if (string.Equals(cond.FieldName, "EventCode", StringComparison.Ordinal))
                {
                    ctx = ctx.With("EventCode", cond.Value ?? string.Empty);
                }
                else if (string.Equals(cond.FieldName, "ActorId", StringComparison.Ordinal))
                {
                    ctx = ctx.With("ActorUserId", cond.Value ?? string.Empty);
                }
            }
        }

        if (input.FromUtc is { } from)
        {
            ctx = ctx.With("CreatedFromUtc", from);
            query = query.Where(a => a.EventAtUtc >= from);
        }
        if (input.ToUtc is { } to)
        {
            ctx = ctx.With("CreatedToUtc", to);
            query = query.Where(a => a.EventAtUtc <= to);
        }

        return (query, ctx, null);
    }

    /// <summary>
    /// Translates a wire <see cref="QbeFilterDto"/> to the server-side
    /// <see cref="QbeFilter"/>. Empty envelope short-circuits to a no-op (null
    /// would have been filtered out by the caller). Operator strings that fail
    /// to parse surface as a sentinel value the converter rejects with a
    /// stable error code.
    /// </summary>
    /// <param name="dto">Wire envelope.</param>
    /// <returns>Mapped filter.</returns>
    private static QbeFilter MapQbe(QbeFilterDto dto)
    {
        var conds = new List<QbeCondition>(dto.Conditions.Count);
        foreach (var c in dto.Conditions)
        {
            if (!Enum.TryParse<QbeOperator>(c.Operator, ignoreCase: false, out var op))
            {
                op = (QbeOperator)int.MinValue;
            }
            conds.Add(new QbeCondition(c.FieldName, op, c.Value, c.Value2));
        }
        return new QbeFilter(
            string.IsNullOrEmpty(dto.Combinator) ? QbeFilter.CombinatorAnd : dto.Combinator,
            conds);
    }

    /// <summary>
    /// Projects an audit-log projection onto the wire <see cref="AuditLogRowDto"/>.
    /// Encodes the row id + the actor / target ids when they look numeric;
    /// surfaces the SHA-256 hash prefix.
    /// </summary>
    /// <param name="row">Source projection.</param>
    /// <returns>Wire DTO.</returns>
    private AuditLogRowDto MapRow(AuditLogProjection row)
    {
        return new AuditLogRowDto(
            Id: _sqids.Encode(row.Id),
            CreatedAtUtc: row.EventAtUtc,
            EventCode: row.EventCode,
            Severity: row.Severity.ToString(),
            ActorUserSqid: EncodeActorSqid(row.ActorId),
            ResourceType: row.TargetEntity,
            ResourceSqid: row.TargetEntityId is { } tid ? _sqids.Encode(tid) : null,
            DetailsJson: row.DetailsJson,
            PrevHashHex: TrimHash(row.PrevHash),
            RowHashHex: TrimHash(row.RowHash));
    }

    /// <summary>
    /// Maps an audit-log projection onto a <see cref="GridRow"/> for the
    /// universal exporter. The column set matches
    /// <see cref="BuildAuditGridColumns"/> exactly so the exporter can look
    /// up every cell.
    /// </summary>
    /// <param name="row">Source projection.</param>
    /// <returns>Grid row.</returns>
    private GridRow MapToGridRow(AuditLogProjection row)
    {
        var cells = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = _sqids.Encode(row.Id),
            ["CreatedAtUtc"] = row.EventAtUtc,
            ["EventCode"] = row.EventCode,
            ["Severity"] = row.Severity.ToString(),
            ["ActorUserSqid"] = EncodeActorSqid(row.ActorId) ?? string.Empty,
            ["ResourceType"] = row.TargetEntity ?? string.Empty,
            ["ResourceSqid"] = row.TargetEntityId is { } tid ? _sqids.Encode(tid) : string.Empty,
            ["DetailsJson"] = row.DetailsJson,
            ["PrevHashHex"] = TrimHash(row.PrevHash),
            ["RowHashHex"] = TrimHash(row.RowHash),
        };
        return new GridRow(cells);
    }

    /// <summary>
    /// Returns the canonical grid-column definitions for the audit grid. The
    /// column names match the field names emitted by <see cref="MapToGridRow"/>
    /// so the exporter can resolve every cell. Headers are RO-only here —
    /// localisation is deferred to the explorer UI follow-up.
    /// </summary>
    /// <returns>Frozen column list.</returns>
    private static IReadOnlyList<GridColumn> BuildAuditGridColumns() => new GridColumn[]
    {
        new(Header: "Cod",        FieldName: "Id",            DataType: GridColumnDataType.Text),
        new(Header: "Eveniment la (UTC)", FieldName: "CreatedAtUtc", DataType: GridColumnDataType.DateTime),
        new(Header: "Cod eveniment", FieldName: "EventCode",   DataType: GridColumnDataType.Text),
        new(Header: "Severitate",  FieldName: "Severity",      DataType: GridColumnDataType.Text),
        new(Header: "Actor",       FieldName: "ActorUserSqid", DataType: GridColumnDataType.Text),
        new(Header: "Tip resursă", FieldName: "ResourceType",  DataType: GridColumnDataType.Text),
        new(Header: "Resursă",     FieldName: "ResourceSqid",  DataType: GridColumnDataType.Text),
        new(Header: "Detalii",     FieldName: "DetailsJson",   DataType: GridColumnDataType.Text),
        new(Header: "Hash anterior", FieldName: "PrevHashHex", DataType: GridColumnDataType.Text),
        new(Header: "Hash rând",   FieldName: "RowHashHex",    DataType: GridColumnDataType.Text),
    };

    /// <summary>
    /// Encodes a numeric actor id to a Sqid string. Non-numeric actor ids
    /// (system / job / anonymous) pass through verbatim — the audit pipeline
    /// accepts both shapes.
    /// </summary>
    /// <param name="actorId">Raw actor id from the audit row.</param>
    /// <returns>Encoded id, or the raw value for non-numeric inputs.</returns>
    private string? EncodeActorSqid(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return null;
        }
        if (long.TryParse(actorId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id >= 0)
        {
            return _sqids.Encode(id);
        }
        return actorId;
    }

    /// <summary>
    /// Returns the first <see cref="HashPrefixLength"/> chars of the supplied
    /// hash digest in lowercase. Returns the input verbatim when shorter than
    /// the prefix; an empty / null input becomes an empty string.
    /// </summary>
    /// <param name="hash">Raw SHA-256 hex digest from the audit row.</param>
    /// <returns>Trimmed prefix.</returns>
    private static string TrimHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return string.Empty;
        }
        var lowered = hash.ToLowerInvariant();
        return lowered.Length <= HashPrefixLength ? lowered : lowered[..HashPrefixLength];
    }

    /// <summary>
    /// Minimal JSON-string escape for the import audit's details payload.
    /// The payload only ever contains the archive key + two integers, so a
    /// surface-level escape of <c>"</c> and <c>\</c> is sufficient — we never
    /// embed user-supplied JSON.
    /// </summary>
    /// <param name="value">Raw string to escape.</param>
    /// <returns>JSON-safe escaped string.</returns>
    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Flat anonymous-class-shaped projection used by EF Core. Lifting to a
    /// real type keeps the EF translation deterministic and lets us materialise
    /// a single row shape that both the page and the grid-export code paths
    /// can share without a second round-trip.
    /// </summary>
    private sealed class AuditLogProjection
    {
        /// <summary>Database primary key.</summary>
        public long Id { get; set; }

        /// <summary>UTC instant of the business event.</summary>
        public DateTime EventAtUtc { get; set; }

        /// <summary>Stable event code.</summary>
        public string EventCode { get; set; } = string.Empty;

        /// <summary>Severity classification.</summary>
        public AuditSeverity Severity { get; set; }

        /// <summary>Actor identifier (string — may carry numeric user id).</summary>
        public string ActorId { get; set; } = string.Empty;

        /// <summary>Affected entity kind (nullable).</summary>
        public string? TargetEntity { get; set; }

        /// <summary>Affected entity primary key (nullable).</summary>
        public long? TargetEntityId { get; set; }

        /// <summary>Structured details — already PII-redacted by the producer.</summary>
        public string DetailsJson { get; set; } = "{}";

        /// <summary>Previous-row SHA-256 digest (lowercase hex) or the GENESIS literal.</summary>
        public string PrevHash { get; set; } = string.Empty;

        /// <summary>This row's SHA-256 digest (lowercase hex).</summary>
        public string RowHash { get; set; } = string.Empty;
    }
}
