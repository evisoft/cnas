using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Treasury.Feed;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Treasury.Feed;

/// <summary>
/// R1810 / TOR BP 1.2-I — production implementation of
/// <see cref="ITreasuryFeedImporter"/>. Drives the lifecycle of one
/// <see cref="TreasuryFeedImport"/> row from <c>Pending</c> through to a
/// terminal state, persisting per-row outcomes as <see cref="TreasuryFeedImportRow"/>
/// children and projecting Imported / Updated rows into the existing
/// <see cref="TreasuryPaymentReceipt"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>No re-use of <c>ITreasuryPaymentService.ImportReceiptAsync</c>.</b> The
/// service-layer entry expects a <c>PayerContributorSqid</c> + raw Idno from
/// the operator; the daily feed knows neither. The importer therefore looks
/// up the payer by IDNO hash + persists the receipt directly via
/// <see cref="ICnasDbContext"/> (mirroring iter 73's ExecutoryDocumentService
/// approach). The reuse seam stays — when a manual import collides with the
/// natural-key uniqueness rule, the importer surfaces the same stable
/// failure messages as the synchronous service.
/// </para>
/// <para>
/// <b>PII safety.</b> The importer never logs payer IDNOs / names / amounts.
/// The audit-row details payload carries only counters; the row's
/// <c>RawPayloadJson</c> is stored in the registry but the JSON is bounded to
/// 4096 chars by the EF configuration and is only accessible to operators
/// who already hold the <c>cnas-admin</c> role.
/// </para>
/// </remarks>
public sealed class TreasuryFeedImporter : ITreasuryFeedImporter
{
    /// <summary>
    /// Default reporting-month strategy: the receipt's calendar month with
    /// day == 1. Treasury feeds do not carry a separate reporting month so the
    /// importer derives one from <see cref="TreasuryFeedParsedRow.ReceiptDate"/>.
    /// </summary>
    /// <param name="receiptDate">The Treasury-side receipt date.</param>
    /// <returns>First-of-month <see cref="DateOnly"/> for the receipt's calendar month.</returns>
    public static DateOnly DeriveReportingMonth(DateOnly receiptDate)
        => new(receiptDate.Year, receiptDate.Month, 1);

    /// <summary>Cached JSON serializer options shared across audit + row payloads.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly ITreasuryFeedSource _source;
    private readonly ITreasuryFeedParser _parser;
    private readonly IDeterministicHasher _idHasher;
    private readonly ILogger<TreasuryFeedImporter> _logger;

    /// <summary>Constructs the importer with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context.</param>
    /// <param name="clock">UTC clock abstraction (CLAUDE.md RULE 4).</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller-context for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="source">Pluggable feed-source adapter.</param>
    /// <param name="parser">CSV parser.</param>
    /// <param name="idHasher">Deterministic IDNO hasher.</param>
    /// <param name="logger">Structured logger.</param>
    public TreasuryFeedImporter(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        ITreasuryFeedSource source,
        ITreasuryFeedParser parser,
        IDeterministicHasher idHasher,
        ILogger<TreasuryFeedImporter> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(idHasher);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _source = source;
        _parser = parser;
        _idHasher = idHasher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<TreasuryFeedImportSummaryDto>> ImportAsync(
        DateOnly feedDate,
        TreasuryFeedTriggerKind trigger,
        CancellationToken cancellationToken = default)
    {
        var actor = _caller.UserSqid ?? "system";
        var now = _clock.UtcNow;

        // Step 1: create the registry row in Pending.
        var import = new TreasuryFeedImport
        {
            FeedDate = feedDate,
            Status = TreasuryFeedImportStatus.Pending,
            SourceKind = TreasuryFeedSourceKind.InMemoryTest,
            StartedAt = now,
            TriggerKind = trigger,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.TreasuryFeedImports.Add(import);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        CnasMeter.TreasuryFeedImportStarted.Add(
            1,
            new KeyValuePair<string, object?>("trigger_kind", trigger.ToString()));

        // Step 2: fetch the file from the configured source.
        import.Status = TreasuryFeedImportStatus.Downloading;
        import.UpdatedAtUtc = _clock.UtcNow;
        import.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var fetch = await _source.FetchAsync(feedDate, cancellationToken).ConfigureAwait(false);
        if (fetch.IsFailure)
        {
            return await FinaliseFailureAsync(import, fetch.ErrorCode!, fetch.ErrorMessage!, actor,
                cancellationToken).ConfigureAwait(false);
        }
        var outcome = fetch.Value;
        import.SourceKind = outcome.SourceKind;
        import.SourceReference = outcome.SourceReference;
        import.FileSizeBytes = outcome.SizeBytes;
        import.FileHashSha256 = outcome.HashSha256;

        // Step 3: parse.
        import.Status = TreasuryFeedImportStatus.Parsing;
        import.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        using (var ms = new MemoryStream(outcome.Content, writable: false))
        {
            var parsed = await _parser.ParseAsync(ms, cancellationToken).ConfigureAwait(false);
            if (parsed.IsFailure)
            {
                return await FinaliseFailureAsync(import, parsed.ErrorCode!, parsed.ErrorMessage!, actor,
                    cancellationToken).ConfigureAwait(false);
            }
            var rows = parsed.Value;
            import.RowsTotal = rows.Count;

            // Step 4: project each row.
            import.Status = TreasuryFeedImportStatus.Importing;
            import.UpdatedAtUtc = _clock.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProjectRowAsync(import, row, actor, cancellationToken).ConfigureAwait(false);
            }
        }

        // Step 5: finalise.
        var completedAt = _clock.UtcNow;
        import.Status = TreasuryFeedImportStatus.Completed;
        import.CompletedAt = completedAt;
        import.UpdatedAtUtc = completedAt;
        import.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitCompletionAuditAsync(import, actor, cancellationToken).ConfigureAwait(false);
        CnasMeter.TreasuryFeedImportCompleted.Add(
            1,
            new KeyValuePair<string, object?>("terminal_status", import.Status.ToString()),
            new KeyValuePair<string, object?>("trigger_kind", trigger.ToString()));

        return Result<TreasuryFeedImportSummaryDto>.Success(ToSummary(import));
    }

    /// <summary>
    /// Projects a single parsed row into the receipt registry and records
    /// the row outcome on the import.
    /// </summary>
    /// <param name="import">Parent import row.</param>
    /// <param name="row">Parsed feed row.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    private async Task ProjectRowAsync(
        TreasuryFeedImport import,
        TreasuryFeedParsedRow row,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var payload = SerialiseRowPayload(row);

        var rowEntity = new TreasuryFeedImportRow
        {
            ImportId = import.Id,
            RowOrdinal = row.RowOrdinal,
            Status = TreasuryFeedImportRowStatus.Pending,
            RawPayloadJson = payload,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.TreasuryFeedImportRows.Add(rowEntity);

        if (row.ParseError is not null)
        {
            rowEntity.Status = TreasuryFeedImportRowStatus.Failed;
            rowEntity.ErrorCode = row.ParseErrorCode ?? "PARSE_ERROR";
            rowEntity.ErrorDescription = row.ParseError;
            rowEntity.ProcessedAt = now;
            import.RowsFailed++;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            CnasMeter.TreasuryFeedRowProcessed.Add(
                1, new KeyValuePair<string, object?>("status", rowEntity.Status.ToString()));
            return;
        }

        // Defensive: parser should guarantee fields populated when ParseError is null.
        var receiptNumber = row.ReceiptNumber!;
        var receiptDate = row.ReceiptDate!.Value;
        var payerIdno = row.PayerIdno!;
        var amount = row.AmountMdl!.Value;

        // Look up the payer by IDNO hash. Treasury feeds use raw IDNOs; the
        // contributor table stores hashes, so the lookup is deterministic.
        var idnoHash = _idHasher.ComputeHash(payerIdno);
        var payerId = await _db.Contributors
            .Where(c => c.IdnoHash == idnoHash && c.IsActive)
            .Select(c => (long?)c.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (payerId is null)
        {
            rowEntity.Status = TreasuryFeedImportRowStatus.Failed;
            rowEntity.ErrorCode = "PAYER_NOT_FOUND";
            rowEntity.ErrorDescription = "No active Contributor exists for the supplied IDNO hash.";
            rowEntity.ProcessedAt = now;
            import.RowsFailed++;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            CnasMeter.TreasuryFeedRowProcessed.Add(
                1, new KeyValuePair<string, object?>("status", rowEntity.Status.ToString()));
            return;
        }

        // Idempotent path: look up an existing receipt by (Number, ReceiptDate).
        var existing = await _db.TreasuryPaymentReceipts
            .SingleOrDefaultAsync(
                r => r.TreasuryReferenceNumber == receiptNumber
                    && r.ReceiptDate == receiptDate
                    && r.IsActive,
                cancellationToken)
            .ConfigureAwait(false);

        var reportingMonth = DeriveReportingMonth(receiptDate);
        if (existing is null)
        {
            // Insert path.
            var receipt = new TreasuryPaymentReceipt
            {
                TreasuryReferenceNumber = receiptNumber,
                ReceiptDate = receiptDate,
                PayerContributorId = payerId.Value,
                ReportingMonth = reportingMonth,
                AmountReceived = amount,
                DistributionStatus = TreasuryPaymentDistributionStatus.Pending,
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
            };
            _db.TreasuryPaymentReceipts.Add(receipt);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            rowEntity.Status = TreasuryFeedImportRowStatus.Imported;
            rowEntity.MappedReceiptId = receipt.Id;
            rowEntity.ProcessedAt = now;
            import.RowsImported++;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            CnasMeter.TreasuryFeedRowProcessed.Add(
                1, new KeyValuePair<string, object?>("status", rowEntity.Status.ToString()));
            return;
        }

        // Existing row — compare content.
        if (existing.PayerContributorId == payerId.Value
            && existing.AmountReceived == amount
            && existing.ReportingMonth == reportingMonth)
        {
            rowEntity.Status = TreasuryFeedImportRowStatus.Skipped;
            rowEntity.MappedReceiptId = existing.Id;
            rowEntity.ProcessedAt = now;
            import.RowsSkipped++;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            CnasMeter.TreasuryFeedRowProcessed.Add(
                1, new KeyValuePair<string, object?>("status", rowEntity.Status.ToString()));
            return;
        }

        // Content drift — update in place. The distribution status stays
        // intact so the distribution job can re-pick the receipt up if it
        // was previously distributed (the existing service rejects re-
        // distribution via the stable ALREADY_DISTRIBUTED message).
        existing.PayerContributorId = payerId.Value;
        existing.AmountReceived = amount;
        existing.ReportingMonth = reportingMonth;
        existing.UpdatedAtUtc = now;
        existing.UpdatedBy = actor;
        rowEntity.Status = TreasuryFeedImportRowStatus.Updated;
        rowEntity.MappedReceiptId = existing.Id;
        rowEntity.ProcessedAt = now;
        import.RowsUpdated++;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        CnasMeter.TreasuryFeedRowProcessed.Add(
            1, new KeyValuePair<string, object?>("status", rowEntity.Status.ToString()));
    }

    /// <summary>
    /// Persists a sanitised, bounded JSON snapshot of the parsed row inputs.
    /// PayerIdno + amount go in (operators need them for forensic replay)
    /// but the JSON is bounded to 4096 chars by the EF configuration.
    /// </summary>
    /// <param name="row">Parsed row.</param>
    /// <returns>JSON snapshot ≤ 4096 chars.</returns>
    private static string SerialiseRowPayload(TreasuryFeedParsedRow row)
    {
        var payload = JsonSerializer.Serialize(new
        {
            row.ReceiptNumber,
            ReceiptDate = row.ReceiptDate?.ToString("O", CultureInfo.InvariantCulture),
            row.PayerIdno,
            row.PayerName,
            AmountMdl = row.AmountMdl?.ToString(CultureInfo.InvariantCulture),
            row.TreasuryCode,
            row.Reference,
        }, CachedJsonOptions);
        // Guard against pathological row inputs that would blow the 4096-byte
        // column. Truncate with an explicit ellipsis marker so operators see
        // the bound was hit.
        const int Max = 4096;
        return payload.Length <= Max ? payload : (payload[..(Max - 3)] + "...");
    }

    /// <summary>
    /// Flips the import to <see cref="TreasuryFeedImportStatus.Failed"/>,
    /// captures the sanitised failure reason, emits the failure audit, and
    /// returns the compact summary so callers don't need to differentiate
    /// failure from success at the result-shape level.
    /// </summary>
    /// <param name="import">Active import row.</param>
    /// <param name="errorCode">Stable error code surfaced by an upstream step.</param>
    /// <param name="errorMessage">Sanitised human description.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>Failed Result carrying the upstream error code.</returns>
    private async Task<Result<TreasuryFeedImportSummaryDto>> FinaliseFailureAsync(
        TreasuryFeedImport import,
        string errorCode,
        string errorMessage,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        import.Status = TreasuryFeedImportStatus.Failed;
        import.CompletedAt = now;
        import.FailureReason = $"{errorCode}: {errorMessage}";
        import.UpdatedAtUtc = now;
        import.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            importSqid = _sqids.Encode(import.Id),
            feedDate = import.FeedDate.ToString("O", CultureInfo.InvariantCulture),
            triggerKind = import.TriggerKind.ToString(),
            errorCode,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            ITreasuryFeedImporter.AuditImportFailed,
            AuditSeverity.Critical,
            actor,
            nameof(TreasuryFeedImport),
            import.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        CnasMeter.TreasuryFeedImportCompleted.Add(
            1,
            new KeyValuePair<string, object?>("terminal_status", import.Status.ToString()),
            new KeyValuePair<string, object?>("trigger_kind", import.TriggerKind.ToString()));

        _logger.LogWarning(
            "Treasury feed import {ImportId} failed at terminal status with code {ErrorCode}.",
            import.Id, errorCode);

        return Result<TreasuryFeedImportSummaryDto>.Failure(errorCode, errorMessage);
    }

    /// <summary>Emits the Information-severity completion audit row.</summary>
    /// <param name="import">Finalised import row.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    private async Task EmitCompletionAuditAsync(
        TreasuryFeedImport import,
        string actor,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            importSqid = _sqids.Encode(import.Id),
            feedDate = import.FeedDate.ToString("O", CultureInfo.InvariantCulture),
            triggerKind = import.TriggerKind.ToString(),
            import.RowsTotal,
            import.RowsImported,
            import.RowsUpdated,
            import.RowsSkipped,
            import.RowsFailed,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            ITreasuryFeedImporter.AuditImportCompleted,
            AuditSeverity.Information,
            actor,
            nameof(TreasuryFeedImport),
            import.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects an entity into its compact summary DTO.
    /// </summary>
    /// <param name="import">Loaded entity.</param>
    /// <returns>Populated summary DTO.</returns>
    private TreasuryFeedImportSummaryDto ToSummary(TreasuryFeedImport import) => new(
        Id: _sqids.Encode(import.Id),
        FeedDate: import.FeedDate,
        Status: import.Status.ToString(),
        RowsTotal: import.RowsTotal,
        RowsImported: import.RowsImported,
        RowsUpdated: import.RowsUpdated,
        RowsSkipped: import.RowsSkipped,
        RowsFailed: import.RowsFailed,
        TriggerKind: import.TriggerKind.ToString());
}
