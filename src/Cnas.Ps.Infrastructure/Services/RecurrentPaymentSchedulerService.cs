using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R1000..R1034 / TOR §3.2-Z — production implementation of
/// <see cref="IRecurrentPaymentSchedulerService"/>. Hosts the schedule
/// registry + the daily run-due primitive that generates
/// <c>MPayOrder</c> rows for every due schedule and advances
/// <c>NextPaymentDate</c> per cadence.
/// </summary>
public sealed class RecurrentPaymentSchedulerService : IRecurrentPaymentSchedulerService
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

    /// <summary>Constructs the service.</summary>
    /// <param name="db">Writer context.</param>
    /// <param name="read">Read-replica context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="sqids">Sqid encoder / decoder.</param>
    /// <param name="caller">Caller context for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    public RecurrentPaymentSchedulerService(
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
    public async Task<Result<RecurrentPaymentScheduleDto>> CreateAsync(
        RecurrentPaymentScheduleCreateInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.BeneficiarySqid))
        {
            return Result<RecurrentPaymentScheduleDto>.Failure(ErrorCodes.ValidationFailed, "BeneficiarySqid is required.");
        }
        if (string.IsNullOrWhiteSpace(input.ServiceCode) || input.ServiceCode.Length > 32)
        {
            return Result<RecurrentPaymentScheduleDto>.Failure(ErrorCodes.ValidationFailed, "ServiceCode must be 1..32 characters.");
        }
        if (input.Amount <= 0m)
        {
            return Result<RecurrentPaymentScheduleDto>.Failure(ErrorCodes.ValidationFailed, "Amount must be > 0.");
        }
        if (!Enum.TryParse<RecurrentPaymentCadence>(input.Cadence, ignoreCase: false, out var cadence))
        {
            return Result<RecurrentPaymentScheduleDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Cadence must be one of Monthly / Quarterly / Annual.");
        }

        var decoded = _sqids.TryDecode(input.BeneficiarySqid);
        if (decoded.IsFailure)
        {
            return Result<RecurrentPaymentScheduleDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        var row = new RecurrentPaymentSchedule
        {
            BeneficiaryId = decoded.Value,
            ServiceCode = input.ServiceCode,
            Amount = input.Amount,
            NextPaymentDate = input.NextPaymentDate,
            Cadence = cadence,
            FailureCount = 0,
            CreatedAtUtc = now,
            CreatedBy = actor,
            IsActive = true,
        };
        _db.RecurrentPaymentSchedules.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IRecurrentPaymentSchedulerService.AuditCreated,
            AuditSeverity.Critical,
            actor,
            row.Id,
            new
            {
                scheduleSqid = _sqids.Encode(row.Id),
                beneficiarySqid = input.BeneficiarySqid,
                input.ServiceCode,
                input.Amount,
                cadence = cadence.ToString(),
                nextPaymentDate = input.NextPaymentDate.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);

        return Result<RecurrentPaymentScheduleDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<int>> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow);
        var due = await _db.RecurrentPaymentSchedules
            .Where(s => s.IsActive && s.NextPaymentDate <= today)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (due.Count == 0)
        {
            return Result<int>.Success(0);
        }

        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "system-scheduler";
        var dispatched = 0;
        // Look up real beneficiary IDNP per schedule. A schedule with no
        // resolvable beneficiary is skipped (logged-equivalent audit row
        // not emitted) — the previous hard-coded "0000000000000" was a TOR
        // CF 18.x compliance issue (rejected by downstream MPay). The
        // service no longer crashes the whole batch on one bad row either.
        var beneficiaryIds = due.Select(s => s.BeneficiaryId).Distinct().ToList();
        var beneficiaryIdnpById = await _db.Solicitants
            .Where(s => beneficiaryIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.NationalId, cancellationToken)
            .ConfigureAwait(false);

        // In-flight check: a schedule whose LastDispatchedOrderId still
        // points at an unconfirmed MPayOrder is already mid-dispatch — the
        // callback advancer hasn't fired yet because the bank hasn't settled.
        // Re-emitting an order would duplicate the obligation. Materialise
        // the set of "in-flight" order ids so we can skip the corresponding
        // schedules below.
        var inFlightOrderIds = due
            .Where(s => s.LastDispatchedOrderId.HasValue)
            .Select(s => s.LastDispatchedOrderId!.Value)
            .Distinct()
            .ToList();
        var inFlightConfirmedById = inFlightOrderIds.Count == 0
            ? new Dictionary<long, DateTime?>()
            : await _db.MPayOrders
                .Where(o => inFlightOrderIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.ConfirmedAtUtc, cancellationToken)
                .ConfigureAwait(false);

        var dispatchedSchedules = new List<RecurrentPaymentSchedule>(due.Count);
        foreach (var s in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Skip schedules whose previous order has not confirmed yet — they
            // are in flight and must wait for the callback advancer to move
            // them forward before we emit another order.
            if (s.LastDispatchedOrderId.HasValue
                && inFlightConfirmedById.TryGetValue(s.LastDispatchedOrderId.Value, out var confirmedAt)
                && confirmedAt is null)
            {
                continue;
            }
            if (!beneficiaryIdnpById.TryGetValue(s.BeneficiaryId, out var idnp) || string.IsNullOrWhiteSpace(idnp))
            {
                // No resolvable beneficiary — increment FailureCount (so ops
                // dashboards surface this), update the audit timestamps, and
                // skip the dispatch. Do NOT advance NextPaymentDate — the
                // next sweep will retry once the beneficiary row exists.
                s.FailureCount += 1;
                s.UpdatedAtUtc = now;
                s.UpdatedBy = actor;
                continue;
            }

            var orderId = $"RPS-{s.Id:D6}-{now:yyyyMMddHHmmss}";
            var order = new MPayOrder
            {
                OrderId = orderId,
                AmountMdl = s.Amount,
                DescriptionRo = $"Plată recurentă {s.ServiceCode}",
                BeneficiaryIdnp = idnp,
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
                // Stays Pending (PaymentRef + ConfirmedAtUtc null) until the
                // MPay callback handler confirms it. NextPaymentDate is NOT
                // advanced here — the IRecurrentPaymentAdvancer (invoked by
                // the callback handler) is the only path that advances the
                // schedule, ensuring the schedule only moves forward when the
                // bank actually settled the payment.
            };
            _db.MPayOrders.Add(order);
            s.UpdatedAtUtc = now;
            s.UpdatedBy = actor;
            // We capture the to-be-emitted order so the second SaveChanges
            // (below, after the FIRST SaveChanges populates order.Id) can
            // back-fill LastDispatchedOrderId. The callback advancer reads
            // this to know which order's confirmation triggers the schedule
            // advance.
            dispatchedSchedules.Add(s);
        }

        // First save: persist the new MPayOrder rows so their database-
        // generated ids are populated. The FailureCount/UpdatedAt mutations
        // on schedules without resolvable beneficiaries land here too.
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Persistence-step failure: increment FailureCount on every
            // schedule we were trying to dispatch so ops sees the failure,
            // then surface a structured Conflict. Do NOT advance
            // NextPaymentDate — the next sweep retries the same rows.
            foreach (var s in dispatchedSchedules)
            {
                s.FailureCount += 1;
                s.UpdatedAtUtc = now;
            }
            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort failure-counter persistence — original ex is the real signal.
            catch
            {
                // If the failure-counter save also fails we cannot recover here.
                // The original exception is re-thrown below so the operator sees
                // the root cause.
            }
#pragma warning restore CA1031
            throw;
        }

        // Second pass: back-fill LastDispatchedOrderId on each dispatched
        // schedule and persist. The MPay callback advancer (invoked from the
        // callback handler) uses this link to know which schedule's
        // NextPaymentDate to advance when a Confirmed order arrives.
        foreach (var s in dispatchedSchedules)
        {
            var emittedOrderId = $"RPS-{s.Id:D6}-{now:yyyyMMddHHmmss}";
            var order = await _db.MPayOrders
                .FirstOrDefaultAsync(o => o.OrderId == emittedOrderId, cancellationToken)
                .ConfigureAwait(false);
            if (order is not null)
            {
                s.LastDispatchedOrderId = order.Id;
            }
            dispatched += 1;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var s in dispatchedSchedules)
        {
            await EmitAuditAsync(
                IRecurrentPaymentSchedulerService.AuditDispatched,
                AuditSeverity.Critical,
                actor,
                s.Id,
                new
                {
                    scheduleSqid = _sqids.Encode(s.Id),
                    s.ServiceCode,
                    amount = s.Amount,
                    // NextPaymentDate is unchanged here — only the callback
                    // advancer moves it forward on confirmation.
                    nextPaymentDate = s.NextPaymentDate.ToString("O", CultureInfo.InvariantCulture),
                    lastDispatchedOrderId = s.LastDispatchedOrderId,
                },
                cancellationToken).ConfigureAwait(false);
        }

        return Result<int>.Success(dispatched);
    }

    /// <inheritdoc />
    public Task<Result<RecurrentPaymentScheduleDto>> SuspendAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default)
        => FlipActiveAsync(
            scheduleSqid,
            targetIsActive: false,
            auditCode: IRecurrentPaymentSchedulerService.AuditSuspended,
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<RecurrentPaymentScheduleDto>> ResumeAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default)
        => FlipActiveAsync(
            scheduleSqid,
            targetIsActive: true,
            auditCode: IRecurrentPaymentSchedulerService.AuditResumed,
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<RecurrentPaymentSchedulePageDto>> ListAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0)
        {
            return Result<RecurrentPaymentSchedulePageDto>.Failure(ErrorCodes.ValidationFailed, "Skip must be ≥ 0.");
        }
        if (take < 1 || take > 100)
        {
            return Result<RecurrentPaymentSchedulePageDto>.Failure(ErrorCodes.ValidationFailed, "Take must be in [1, 100].");
        }

        var total = await _read.RecurrentPaymentSchedules.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await _read.RecurrentPaymentSchedules
            .OrderBy(s => s.NextPaymentDate)
            .ThenBy(s => s.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result<RecurrentPaymentSchedulePageDto>.Success(new RecurrentPaymentSchedulePageDto(
            Items: rows.Select(ToDto).ToList(),
            Total: total,
            Skip: skip,
            Take: take));
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(
        string scheduleSqid,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(scheduleSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var row = loaded.Value;
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        row.IsActive = false;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Loads a schedule by Sqid; returns a friendly failure on bad input or missing row.</summary>
    /// <param name="scheduleSqid">Sqid-encoded id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The loaded entity on success.</returns>
    private async Task<Result<RecurrentPaymentSchedule>> LoadAsync(
        string scheduleSqid,
        CancellationToken cancellationToken)
    {
        var decoded = _sqids.TryDecode(scheduleSqid);
        if (decoded.IsFailure)
        {
            return Result<RecurrentPaymentSchedule>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var row = await _db.RecurrentPaymentSchedules
            .FirstOrDefaultAsync(s => s.Id == decoded.Value, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? Result<RecurrentPaymentSchedule>.Failure(
                IRecurrentPaymentSchedulerService.ScheduleNotFoundCode,
                $"Recurrent-payment schedule '{scheduleSqid}' not found.")
            : Result<RecurrentPaymentSchedule>.Success(row);
    }

    /// <summary>
    /// Flips the IsActive flag, persists the change, emits the appropriate
    /// audit event.
    /// </summary>
    /// <param name="scheduleSqid">Sqid-encoded id.</param>
    /// <param name="targetIsActive">Target value.</param>
    /// <param name="auditCode">Audit event code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated DTO on success.</returns>
    private async Task<Result<RecurrentPaymentScheduleDto>> FlipActiveAsync(
        string scheduleSqid,
        bool targetIsActive,
        string auditCode,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(scheduleSqid, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<RecurrentPaymentScheduleDto>.Failure(loaded.ErrorCode!, loaded.ErrorMessage!);
        }
        var row = loaded.Value;
        if (row.IsActive == targetIsActive)
        {
            return Result<RecurrentPaymentScheduleDto>.Failure(
                IRecurrentPaymentSchedulerService.InvalidTransitionCode,
                $"Schedule is already {(targetIsActive ? "Active" : "Suspended")}.");
        }
        row.IsActive = targetIsActive;
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        row.UpdatedAtUtc = now;
        row.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            auditCode,
            AuditSeverity.Critical,
            actor,
            row.Id,
            new
            {
                scheduleSqid = _sqids.Encode(row.Id),
                row.ServiceCode,
                isActive = targetIsActive,
            },
            cancellationToken).ConfigureAwait(false);

        return Result<RecurrentPaymentScheduleDto>.Success(ToDto(row));
    }

    /// <summary>Advances a <see cref="DateOnly"/> by one cadence step.</summary>
    /// <param name="from">Starting date.</param>
    /// <param name="cadence">Cadence enum value.</param>
    /// <returns>The advanced date.</returns>
    private static DateOnly AdvanceByCadence(DateOnly from, RecurrentPaymentCadence cadence)
        => cadence switch
        {
            RecurrentPaymentCadence.Monthly => from.AddMonths(1),
            RecurrentPaymentCadence.Quarterly => from.AddMonths(3),
            RecurrentPaymentCadence.Annual => from.AddMonths(12),
            _ => from.AddMonths(1),
        };

    /// <summary>Projects an entity into its outbound DTO.</summary>
    /// <param name="s">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private RecurrentPaymentScheduleDto ToDto(RecurrentPaymentSchedule s) => new(
        Id: _sqids.Encode(s.Id),
        BeneficiarySqid: _sqids.Encode(s.BeneficiaryId),
        ServiceCode: s.ServiceCode,
        Amount: s.Amount,
        NextPaymentDate: s.NextPaymentDate,
        Cadence: s.Cadence.ToString(),
        IsActive: s.IsActive,
        LastPaymentAtUtc: s.LastPaymentAtUtc,
        FailureCount: s.FailureCount);

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Anonymous object serialised to JSON.</param>
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
            nameof(RecurrentPaymentSchedule),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }
}
