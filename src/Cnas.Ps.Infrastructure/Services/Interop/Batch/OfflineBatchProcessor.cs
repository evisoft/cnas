using System.Globalization;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Interop;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Interop;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cnas.Ps.Infrastructure.Services.Interop.Batch;

/// <summary>
/// R1710 / TOR INT 002 — production implementation of
/// <see cref="IOfflineBatchProcessor"/>. Runs one <c>Queued</c> submission
/// end-to-end: dispatches per-row to <see cref="IInteropApi"/>, builds the
/// response CSV, hashes + signs it, and finalises the submission row.
/// </summary>
public sealed class OfflineBatchProcessor : IOfflineBatchProcessor
{
    /// <summary>Stable audit event code emitted when a batch completes successfully.</summary>
    public const string AuditBatchCompleted = "BATCH.COMPLETED";

    /// <summary>Stable audit event code emitted when processing crashes.</summary>
    public const string AuditBatchProcessingFailed = "BATCH.PROCESSING_FAILED";

    /// <summary>Stable conflict message when the submission is not in <c>Queued</c>.</summary>
    public const string NotQueuedMessage = "BATCH_NOT_QUEUED";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IOfflineBatchBlobStore _blobs;
    private readonly IOfflineBatchOpSchemaRegistry _schemas;
    private readonly IBatchResponseSigner _signer;
    private readonly IInteropApi _interop;
    private readonly ILogger<OfflineBatchProcessor> _logger;

    /// <summary>Constructs the processor.</summary>
    /// <param name="db">EF writer context.</param>
    /// <param name="clock">UTC clock.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Caller context.</param>
    /// <param name="audit">Audit façade.</param>
    /// <param name="blobs">Byte-blob storage.</param>
    /// <param name="schemas">Op-schema registry.</param>
    /// <param name="signer">Response-CSV signer.</param>
    /// <param name="interop">Synchronous Annex-4 API.</param>
    /// <param name="logger">Structured logger.</param>
    public OfflineBatchProcessor(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IOfflineBatchBlobStore blobs,
        IOfflineBatchOpSchemaRegistry schemas,
        IBatchResponseSigner signer,
        IInteropApi interop,
        ILogger<OfflineBatchProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(interop);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _blobs = blobs;
        _schemas = schemas;
        _signer = signer;
        _interop = interop;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<OfflineBatchSubmissionDto>> ProcessAsync(
        string submissionSqid,
        CancellationToken cancellationToken = default)
    {
        var decoded = _sqids.TryDecode(submissionSqid);
        if (decoded.IsFailure)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var sub = await _db.OfflineBatchSubmissions
            .FirstOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (sub is null)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(ErrorCodes.NotFound, "Submission not found.");
        }
        if (sub.Status != OfflineBatchStatus.Queued)
        {
            return Result<OfflineBatchSubmissionDto>.Failure(ErrorCodes.Conflict, NotQueuedMessage);
        }

        sub.Status = OfflineBatchStatus.Running;
        sub.StartedAt = _clock.UtcNow;
        sub.UpdatedAtUtc = _clock.UtcNow;
        sub.UpdatedBy = "system";
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var schema = _schemas.Get(sub.OpCode);
            var rows = await _db.OfflineBatchRows
                .Where(r => r.SubmissionId == sub.Id && r.IsActive)
                .OrderBy(r => r.RowOrdinal)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            int succeeded = 0;
            int failed = 0;
            var responseCells = new List<List<string>>(capacity: rows.Count);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Pre-failed by the parser — skip the interop call.
                if (row.Status == OfflineBatchRowStatus.Failed)
                {
                    failed++;
                    responseCells.Add(BuildFailedRowCells(schema, row));
                    CnasMeter.OfflineBatchRowProcessed.Add(1,
                        new KeyValuePair<string, object?>("op_code", sub.OpCode.ToString()),
                        new KeyValuePair<string, object?>("status", "Failed"));
                    continue;
                }

                var dispatch = await DispatchAsync(sub.OpCode, row.RequestPayloadJson, cancellationToken)
                    .ConfigureAwait(false);
                row.ProcessedAt = _clock.UtcNow;
                row.UpdatedAtUtc = _clock.UtcNow;
                row.UpdatedBy = "system";

                if (dispatch.IsSuccess)
                {
                    row.Status = OfflineBatchRowStatus.Succeeded;
                    row.ResponsePayloadJson = dispatch.ResponseJson;
                    row.ErrorCode = null;
                    row.ErrorDescription = null;
                    succeeded++;
                    responseCells.Add(BuildSucceededRowCells(schema, row, dispatch.ResponseJson));
                }
                else
                {
                    row.Status = OfflineBatchRowStatus.Failed;
                    row.ErrorCode = dispatch.ErrorCode;
                    row.ErrorDescription = SanitiseDescription(dispatch.ErrorMessage);
                    failed++;
                    responseCells.Add(BuildFailedRowCells(schema, row));
                }
                CnasMeter.OfflineBatchRowProcessed.Add(1,
                    new KeyValuePair<string, object?>("op_code", sub.OpCode.ToString()),
                    new KeyValuePair<string, object?>("status", row.Status.ToString()));
            }
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Build the response CSV.
            var csvBytes = BuildResponseCsv(schema, responseCells);
            var responseKey = await _blobs.PutAsync(csvBytes, "text/csv", cancellationToken)
                .ConfigureAwait(false);
            var responseHash = OfflineBatchSubmissionService.ComputeSha256Hex(csvBytes);
            var signature = await _signer.SignAsync(csvBytes, cancellationToken).ConfigureAwait(false);

            sub.Status = OfflineBatchStatus.Completed;
            sub.CompletedAt = _clock.UtcNow;
            sub.ResponseFileStorageKey = responseKey;
            sub.ResponseFileHashSha256 = responseHash;
            sub.ResponseFileSignatureBase64 = signature;
            sub.TotalRowsProcessed = succeeded + failed;
            sub.TotalRowsFailed = failed;
            sub.UpdatedAtUtc = _clock.UtcNow;
            sub.UpdatedBy = "system";
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            CnasMeter.OfflineBatchCompleted.Add(1,
                new KeyValuePair<string, object?>("op_code", sub.OpCode.ToString()),
                new KeyValuePair<string, object?>("terminal_status", "Completed"));

            await _audit.RecordAsync(
                AuditBatchCompleted,
                AuditSeverity.Information,
                actorId: "system",
                targetEntity: nameof(OfflineBatchSubmission),
                targetEntityId: sub.Id,
                detailsJson: JsonSerializer.Serialize(new
                {
                    batchNumber = sub.BatchNumber,
                    opCode = sub.OpCode.ToString(),
                    totalProcessed = sub.TotalRowsProcessed,
                    totalFailed = sub.TotalRowsFailed,
                }),
                sourceIp: null,
                correlationId: _caller.CorrelationId,
                cancellationToken).ConfigureAwait(false);

            return Result<OfflineBatchSubmissionDto>.Success(ToDto(sub));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sub.Status = OfflineBatchStatus.Failed;
            sub.CompletedAt = _clock.UtcNow;
            sub.FailureReason = SanitiseDescription(ex.GetType().Name + ": " + ex.Message, max: 1000);
            sub.UpdatedAtUtc = _clock.UtcNow;
            sub.UpdatedBy = "system";
            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception inner)
            {
                _logger.LogError(inner, "Failed to persist Failed-state transition on submission {Id}.", sub.Id);
            }

            CnasMeter.OfflineBatchCompleted.Add(1,
                new KeyValuePair<string, object?>("op_code", sub.OpCode.ToString()),
                new KeyValuePair<string, object?>("terminal_status", "Failed"));

            try
            {
                await _audit.RecordAsync(
                    AuditBatchProcessingFailed,
                    AuditSeverity.Critical,
                    actorId: "system",
                    targetEntity: nameof(OfflineBatchSubmission),
                    targetEntityId: sub.Id,
                    detailsJson: JsonSerializer.Serialize(new
                    {
                        batchNumber = sub.BatchNumber,
                        opCode = sub.OpCode.ToString(),
                        exceptionType = ex.GetType().Name,
                    }),
                    sourceIp: null,
                    correlationId: _caller.CorrelationId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception auditEx)
            {
                _logger.LogError(auditEx, "Failed to emit BATCH.PROCESSING_FAILED audit for submission {Id}.", sub.Id);
            }
            return Result<OfflineBatchSubmissionDto>.Failure(ErrorCodes.Internal, "Processing failed.");
        }
    }

    /// <summary>
    /// Dispatches one parsed row payload to the appropriate
    /// <see cref="IInteropApi"/> method. Returns a uniform dispatch result
    /// the per-row loop branches on.
    /// </summary>
    private async Task<DispatchOutcome> DispatchAsync(
        AnnexFourBatchOp opCode,
        string requestJson,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            switch (opCode)
            {
                case AnnexFourBatchOp.GetInsuredPersonStatus:
                {
                    var idnp = root.GetProperty("Idnp").GetString() ?? string.Empty;
                    var r = await _interop.GetInsuredPersonStatusAsync(idnp, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.GetContributionHistory:
                {
                    var idnp = root.GetProperty("Idnp").GetString() ?? string.Empty;
                    var from = DateOnly.Parse(root.GetProperty("FromMonth").GetString() ?? string.Empty, CultureInfo.InvariantCulture);
                    var to = DateOnly.Parse(root.GetProperty("ToMonth").GetString() ?? string.Empty, CultureInfo.InvariantCulture);
                    var r = await _interop.GetContributionHistoryAsync(idnp, from, to, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.GetBenefitsList:
                {
                    var idnp = root.GetProperty("Idnp").GetString() ?? string.Empty;
                    var r = await _interop.GetBenefitsListAsync(idnp, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.GetPersonalAccountSnapshot:
                {
                    var idnp = root.GetProperty("Idnp").GetString() ?? string.Empty;
                    var r = await _interop.GetPersonalAccountSnapshotAsync(idnp, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.GetActiveDecisions:
                {
                    var idnp = root.GetProperty("Idnp").GetString() ?? string.Empty;
                    var r = await _interop.GetActiveDecisionsAsync(idnp, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.GetPaymentStatus:
                {
                    var sqid = root.GetProperty("DecisionSqid").GetString() ?? string.Empty;
                    var period = DateOnly.Parse(root.GetProperty("Period").GetString() ?? string.Empty, CultureInfo.InvariantCulture);
                    var r = await _interop.GetPaymentStatusAsync(sqid, period, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.GetPayerData:
                {
                    var code = root.GetProperty("TaxpayerCode").GetString() ?? string.Empty;
                    var r = await _interop.GetPayerDataAsync(code, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.IsBenefitBeneficiary:
                {
                    var idnp = root.GetProperty("Idnp").GetString() ?? string.Empty;
                    var btype = root.GetProperty("BenefitType").GetString() ?? string.Empty;
                    var r = await _interop.IsBenefitBeneficiaryAsync(idnp, btype, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.GetContributionPaymentInfo:
                {
                    var idno = root.GetProperty("Idno").GetString() ?? string.Empty;
                    var period = DateOnly.Parse(root.GetProperty("Period").GetString() ?? string.Empty, CultureInfo.InvariantCulture);
                    var r = await _interop.GetContributionPaymentInfoAsync(idno, period, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.GetLegalApplicableForm:
                {
                    var idnp = root.GetProperty("Idnp").GetString() ?? string.Empty;
                    var code = root.GetProperty("AgreementCode").GetString() ?? string.Empty;
                    var r = await _interop.GetLegalApplicableFormAsync(idnp, code, ct).ConfigureAwait(false);
                    return Project(r);
                }
                case AnnexFourBatchOp.GetWorkInsurancePeriod:
                {
                    var idnp = root.GetProperty("Idnp").GetString() ?? string.Empty;
                    var r = await _interop.GetWorkInsurancePeriodAsync(idnp, ct).ConfigureAwait(false);
                    return Project(r);
                }
                default:
                    return new DispatchOutcome(false, null, ErrorCodes.ValidationFailed, "Unknown op code.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DispatchOutcome(false, null, ErrorCodes.ValidationFailed, "Row payload could not be dispatched.");
        }
    }

    /// <summary>Lifts a successful <see cref="Result{T}"/> into a dispatch outcome.</summary>
    private static DispatchOutcome Project<T>(Result<T> r)
        => r.IsSuccess
            ? new DispatchOutcome(true, JsonSerializer.Serialize(r.Value, JsonOpts), null, null)
            : new DispatchOutcome(false, null, r.ErrorCode, r.ErrorMessage);

    /// <summary>Returns the response-CSV cells for a successful row.</summary>
    private static List<string> BuildSucceededRowCells(OfflineBatchOpSchema schema, OfflineBatchRow row, string? responseJson)
    {
        var opCells = schema.SerializeResponseRow(responseJson);
        var cells = new List<string>(opCells.Count + 3)
        {
            row.RowOrdinal.ToString(CultureInfo.InvariantCulture),
            row.Status.ToString(),
            string.Empty,
        };
        cells.AddRange(opCells);
        return cells;
    }

    /// <summary>Returns the response-CSV cells for a failed row.</summary>
    private static List<string> BuildFailedRowCells(OfflineBatchOpSchema schema, OfflineBatchRow row)
    {
        var opCells = schema.SerializeResponseRow(null);
        var cells = new List<string>(opCells.Count + 3)
        {
            row.RowOrdinal.ToString(CultureInfo.InvariantCulture),
            row.Status.ToString(),
            row.ErrorCode ?? string.Empty,
        };
        cells.AddRange(opCells);
        return cells;
    }

    /// <summary>Builds the response CSV byte array — header + every row joined with line feeds.</summary>
    private static byte[] BuildResponseCsv(OfflineBatchOpSchema schema, List<List<string>> rowCells)
    {
        var sb = new StringBuilder();
        sb.AppendLine(OfflineBatchRequestParser.FormatCsvLine(schema.ResponseHeader));
        foreach (var row in rowCells)
        {
            sb.AppendLine(OfflineBatchRequestParser.FormatCsvLine(row));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Sanitises an error message for persistence — strips digit runs that could be IDNP/IDNO fragments.</summary>
    private static string SanitiseDescription(string? message, int max = 500)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Row dispatch failed.";
        }
        var trimmed = message.Length > max ? message[..max] : message;
        var sb = new StringBuilder(trimmed.Length);
        int digitRun = 0;
        int runStart = -1;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (char.IsDigit(trimmed[i]))
            {
                if (digitRun == 0) { runStart = i; }
                digitRun++;
            }
            else
            {
                FlushRun(trimmed, runStart, digitRun, sb);
                digitRun = 0;
                sb.Append(trimmed[i]);
            }
        }
        FlushRun(trimmed, runStart, digitRun, sb);
        return sb.ToString();
    }

    /// <summary>Helper for <see cref="SanitiseDescription"/> — replaces 8+ digit runs with a redaction marker.</summary>
    private static void FlushRun(string source, int runStart, int digitRun, StringBuilder sb)
    {
        if (digitRun == 0) { return; }
        if (digitRun >= 8)
        {
            sb.Append("[REDACTED]");
        }
        else
        {
            sb.Append(source, runStart, digitRun);
        }
    }

    /// <summary>Maps a submission row to its outbound DTO using the same projection as the submission service.</summary>
    private OfflineBatchSubmissionDto ToDto(OfflineBatchSubmission sub)
        => new(
            Id: _sqids.Encode(sub.Id),
            BatchNumber: sub.BatchNumber,
            ConsumerSubject: sub.ConsumerSubject,
            OpCode: sub.OpCode.ToString(),
            Status: sub.Status.ToString(),
            RequestFileName: sub.RequestFileName,
            RequestFileSizeBytes: sub.RequestFileSizeBytes,
            RequestFileHashSha256: sub.RequestFileHashSha256,
            RequestRowCount: sub.RequestRowCount,
            ResponseFileHashSha256: sub.ResponseFileHashSha256,
            ResponseFileSignatureBase64: sub.ResponseFileSignatureBase64,
            SubmittedAt: sub.SubmittedAt,
            StartedAt: sub.StartedAt,
            CompletedAt: sub.CompletedAt,
            FailureReason: sub.FailureReason,
            TotalRowsProcessed: sub.TotalRowsProcessed,
            TotalRowsFailed: sub.TotalRowsFailed);

    /// <summary>One per-row dispatch result.</summary>
    /// <param name="IsSuccess">Whether the underlying interop call succeeded.</param>
    /// <param name="ResponseJson">JSON snapshot of the response DTO when <see cref="IsSuccess"/> is <c>true</c>.</param>
    /// <param name="ErrorCode">Stable error code when <see cref="IsSuccess"/> is <c>false</c>.</param>
    /// <param name="ErrorMessage">Description of the failure when <see cref="IsSuccess"/> is <c>false</c>.</param>
    private sealed record DispatchOutcome(
        bool IsSuccess,
        string? ResponseJson,
        string? ErrorCode,
        string? ErrorMessage);
}
