using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Documents;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Documents;

/// <summary>
/// R0602 / TOR CF 11.03 — default <see cref="IPaperFulfilmentService"/>
/// implementation. Drives the
/// <see cref="PaperFulfilmentRecord"/> state machine
/// (Pending → Printed → Dispatched → Delivered) and emits a Notice-severity
/// audit row on every transition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit events.</b>
/// <c>PAPER.ENQUEUED</c>, <c>PAPER.PRINTED</c>, <c>PAPER.DISPATCHED</c>,
/// <c>PAPER.DELIVERED</c> — each with the fulfilment row id as target.
/// </para>
/// </remarks>
public sealed class PaperFulfilmentService : IPaperFulfilmentService
{
    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">EF Core write context.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC clock.</param>
    /// <param name="caller">Authenticated caller context.</param>
    /// <param name="audit">Audit sink.</param>
    public PaperFulfilmentService(
        ICnasDbContext db,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<PaperFulfilmentDto>> EnqueueAsync(
        string documentSqid,
        string territorialSubdivisionCode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentSqid);
        ArgumentException.ThrowIfNullOrWhiteSpace(territorialSubdivisionCode);

        var decoded = _sqids.TryDecode(documentSqid);
        if (decoded.IsFailure)
        {
            return Result<PaperFulfilmentDto>.Failure(ErrorCodes.InvalidSqid, decoded.ErrorMessage!);
        }
        var documentId = decoded.Value;

        var documentExists = await _db.Documents
            .Where(d => d.Id == documentId && d.IsActive)
            .AnyAsync(ct).ConfigureAwait(false);
        if (!documentExists)
        {
            return Result<PaperFulfilmentDto>.Failure(
                ErrorCodes.NotFound,
                $"Document '{documentSqid}' not found.");
        }

        var alreadyEnqueued = await _db.PaperFulfilmentRecords
            .Where(p => p.DocumentId == documentId && p.IsActive)
            .AnyAsync(ct).ConfigureAwait(false);
        if (alreadyEnqueued)
        {
            return Result<PaperFulfilmentDto>.Failure(
                ErrorCodes.PaperFulfilmentAlreadyEnqueued,
                $"Document '{documentSqid}' already has an active paper fulfilment row.");
        }

        var now = _clock.UtcNow;
        var row = new PaperFulfilmentRecord
        {
            DocumentId = documentId,
            TerritorialSubdivisionCode = territorialSubdivisionCode,
            Status = PaperFulfilmentStatus.Pending,
            EnqueuedAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.PaperFulfilmentRecords.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            "PAPER.ENQUEUED",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "system",
            nameof(PaperFulfilmentRecord),
            row.Id,
            $"{{\"documentId\":{documentId},\"subdivision\":\"{territorialSubdivisionCode}\"}}",
            sourceIp: null,
            correlationId: null,
            ct).ConfigureAwait(false);

        return Result<PaperFulfilmentDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result> MarkPrintedAsync(string sqid, CancellationToken ct = default)
    {
        var loaded = await LoadAsync(sqid, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var row = loaded.Value;

        if (row.Status != PaperFulfilmentStatus.Pending)
        {
            return Result.Failure(
                ErrorCodes.PaperFulfilmentInvalidTransition,
                $"Cannot mark Printed; current status is {row.Status}.");
        }

        row.Status = PaperFulfilmentStatus.Printed;
        row.PrintedAtUtc = _clock.UtcNow;
        row.UpdatedAtUtc = row.PrintedAtUtc;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            "PAPER.PRINTED",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "system",
            nameof(PaperFulfilmentRecord),
            row.Id,
            $"{{\"documentId\":{row.DocumentId}}}",
            sourceIp: null,
            correlationId: null,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> MarkDispatchedAsync(
        string sqid,
        string carrierTrackingNumber,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(carrierTrackingNumber);

        var loaded = await LoadAsync(sqid, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var row = loaded.Value;

        if (row.Status != PaperFulfilmentStatus.Printed)
        {
            return Result.Failure(
                ErrorCodes.PaperFulfilmentInvalidTransition,
                $"Cannot mark Dispatched; current status is {row.Status}.");
        }

        var now = _clock.UtcNow;
        row.Status = PaperFulfilmentStatus.Dispatched;
        row.DispatchedAtUtc = now;
        row.CarrierTrackingNumber = carrierTrackingNumber.Length > 64
            ? carrierTrackingNumber[..64]
            : carrierTrackingNumber;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            "PAPER.DISPATCHED",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "system",
            nameof(PaperFulfilmentRecord),
            row.Id,
            $"{{\"documentId\":{row.DocumentId},\"tracking\":\"{row.CarrierTrackingNumber}\"}}",
            sourceIp: null,
            correlationId: null,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> MarkDeliveredAsync(
        string sqid,
        DateOnly deliveredOn,
        CancellationToken ct = default)
    {
        var loaded = await LoadAsync(sqid, ct).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var row = loaded.Value;

        if (row.Status != PaperFulfilmentStatus.Dispatched)
        {
            return Result.Failure(
                ErrorCodes.PaperFulfilmentInvalidTransition,
                $"Cannot mark Delivered; current status is {row.Status}.");
        }

        var now = _clock.UtcNow;
        row.Status = PaperFulfilmentStatus.Delivered;
        row.DeliveredOn = deliveredOn;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            "PAPER.DELIVERED",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "system",
            nameof(PaperFulfilmentRecord),
            row.Id,
            $"{{\"documentId\":{row.DocumentId},\"deliveredOn\":\"{deliveredOn:O}\"}}",
            sourceIp: null,
            correlationId: null,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>Loads a paper-fulfilment row by its Sqid. Returns <see cref="ErrorCodes.NotFound"/> when missing.</summary>
    /// <param name="sqid">Sqid-encoded id of the fulfilment row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The loaded row or a failure.</returns>
    private async Task<Result<PaperFulfilmentRecord>> LoadAsync(string sqid, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqid);
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<PaperFulfilmentRecord>.Failure(ErrorCodes.InvalidSqid, decoded.ErrorMessage!);
        }
        var row = await _db.PaperFulfilmentRecords
            .Where(p => p.Id == decoded.Value && p.IsActive)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result<PaperFulfilmentRecord>.Failure(
                ErrorCodes.NotFound,
                $"PaperFulfilmentRecord '{sqid}' not found.");
        }
        return Result<PaperFulfilmentRecord>.Success(row);
    }

    /// <summary>Projects the entity row to its wire DTO.</summary>
    /// <param name="row">The entity row.</param>
    /// <returns>The wire DTO.</returns>
    private PaperFulfilmentDto ToDto(PaperFulfilmentRecord row) => new(
        _sqids.Encode(row.Id),
        _sqids.Encode(row.DocumentId),
        row.TerritorialSubdivisionCode,
        StatusName(row.Status),
        row.EnqueuedAtUtc,
        row.PrintedAtUtc,
        row.DispatchedAtUtc,
        row.DeliveredOn,
        row.CarrierTrackingNumber);

    /// <summary>Maps a domain enum value to its stable wire name.</summary>
    /// <param name="status">Domain enum value.</param>
    /// <returns>The stable wire string.</returns>
    private static string StatusName(PaperFulfilmentStatus status) => status switch
    {
        PaperFulfilmentStatus.Pending => PaperFulfilmentStatusNames.Pending,
        PaperFulfilmentStatus.Printed => PaperFulfilmentStatusNames.Printed,
        PaperFulfilmentStatus.Dispatched => PaperFulfilmentStatusNames.Dispatched,
        PaperFulfilmentStatus.Delivered => PaperFulfilmentStatusNames.Delivered,
        _ => "Unknown",
    };
}
