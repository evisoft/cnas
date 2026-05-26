using System;
using System.Globalization;
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
/// R1000..R1034 / TOR §3.2-AB..AD — production implementation of
/// <see cref="IVoucherQuotaService"/>. Hosts the configure / check /
/// reserve / release primitives that gate the spa / rehabilitation /
/// sanatorium passports (3.2-AB / 3.2-AC / 3.2-AD).
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomicity.</b> Reservation increments + cap re-check happen inside a
/// single <c>SaveChangesAsync</c> call so EF's xmin concurrency token
/// blocks a double-spend race; a concurrent reservation that materialises
/// the same row trips a <c>DbUpdateConcurrencyException</c> which the
/// caller may retry.
/// </para>
/// <para>
/// <b>Month rollover.</b> The first reservation in a new calendar month
/// implicitly resets <c>UsedThisMonth</c> to <c>0</c> and updates
/// <c>UsedMonth</c> to the new month — no separate sweep job is required.
/// The annual counter accumulates across months and resets only when the
/// row's <c>Year</c> field rolls over (a new row is created for the next
/// year).
/// </para>
/// </remarks>
public sealed class VoucherQuotaService : IVoucherQuotaService
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
    public VoucherQuotaService(
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
    public async Task<Result<VoucherQuotaCheckDto>> CheckAvailabilityAsync(
        string passportCode,
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        var guard = GuardArgs(passportCode, year, month);
        if (guard.IsFailure)
        {
            return Result<VoucherQuotaCheckDto>.Failure(guard.ErrorCode!, guard.ErrorMessage!);
        }

        var row = await _read.VoucherQuotas
            .Where(q => q.PassportCode == passportCode && q.Year == year && q.IsActive)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<VoucherQuotaCheckDto>.Failure(
                IVoucherQuotaService.QuotaNotConfiguredCode,
                $"No voucher quota configured for passport '{passportCode}' year {year}.");
        }

        var monthlyRemaining = ComputeMonthlyRemaining(row, month);
        var annualRemaining = ComputeAnnualRemaining(row);
        var available = monthlyRemaining > 0 && annualRemaining > 0;

        return Result<VoucherQuotaCheckDto>.Success(new VoucherQuotaCheckDto(
            PassportCode: row.PassportCode,
            Year: row.Year,
            Month: month,
            MonthlyRemaining: monthlyRemaining,
            AnnualRemaining: annualRemaining,
            IsAvailable: available));
    }

    /// <summary>
    /// Maximum number of attempts the reserve path will make against a
    /// <see cref="DbUpdateConcurrencyException"/>. The first attempt is the
    /// optimistic happy path; the retry covers the realistic "two reservations
    /// race on the xmin token" case. A third attempt would indicate sustained
    /// contention that the caller should observe as a structured Conflict.
    /// </summary>
    private const int ReserveCasMaxAttempts = 2;

    /// <inheritdoc />
    public async Task<Result> ReserveAsync(
        string passportCode,
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        var guard = GuardArgs(passportCode, year, month);
        if (guard.IsFailure)
        {
            return guard;
        }

        // Bounded retry on the xmin concurrency token. Two concurrent reservers
        // racing on the same row both pass the local cap-check, both increment
        // counters, and the loser's SaveChanges throws
        // DbUpdateConcurrencyException. On retry we re-read the row through a
        // cleared change-tracker, re-evaluate the caps (the winning peer has
        // already incremented), and either retry the reservation or surface
        // QuotaExhausted now that the second slot is gone.
        for (var attempt = 0; attempt < ReserveCasMaxAttempts; attempt++)
        {
            var row = await _db.VoucherQuotas
                .Where(q => q.PassportCode == passportCode && q.Year == year && q.IsActive)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                return Result.Failure(
                    IVoucherQuotaService.QuotaNotConfiguredCode,
                    $"No voucher quota configured for passport '{passportCode}' year {year}.");
            }

            // Reset monthly counter on month rollover.
            if (row.UsedMonth != month)
            {
                row.UsedMonth = month;
                row.UsedThisMonth = 0;
            }

            var monthlyRemaining = row.MonthlyQuota == 0
                ? int.MaxValue
                : row.MonthlyQuota - row.UsedThisMonth;
            var annualRemaining = row.AnnualQuota == 0
                ? int.MaxValue
                : row.AnnualQuota - row.UsedThisYear;
            if (monthlyRemaining <= 0 || annualRemaining <= 0)
            {
                return Result.Failure(
                    IVoucherQuotaService.QuotaExhaustedCode,
                    $"Voucher quota exhausted for passport '{passportCode}' year {year} month {month}.");
            }

            row.UsedThisMonth += 1;
            row.UsedThisYear += 1;
            var now = _clock.UtcNow;
            var actor = _caller.UserSqid ?? "admin";
            row.UpdatedAtUtc = now;
            row.UpdatedBy = actor;

            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Drop the stale snapshot so the next iteration reloads the
                // row fresh from the DB (the winning peer's increments are
                // visible). Bounded retry budget surfaces sustained contention
                // as Conflict on the last attempt.
                if (_db is DbContext concrete)
                {
                    concrete.ChangeTracker.Clear();
                }
                if (attempt + 1 >= ReserveCasMaxAttempts)
                {
                    return Result.Failure(
                        ErrorCodes.Conflict,
                        $"Voucher quota row contended for passport '{passportCode}' year {year}; retry the reservation.");
                }
                continue;
            }

            await EmitAuditAsync(
                IVoucherQuotaService.AuditReserved,
                AuditSeverity.Sensitive,
                actor,
                row.Id,
                new
                {
                    passportCode,
                    year,
                    month,
                    usedThisMonth = row.UsedThisMonth,
                    usedThisYear = row.UsedThisYear,
                },
                cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }

        // Unreachable — the loop either returns Success on the try path or
        // returns Conflict on the final-attempt catch. Defensive fallback.
        return Result.Failure(
            ErrorCodes.Conflict,
            $"Voucher quota row contended for passport '{passportCode}' year {year}; retry the reservation.");
    }

    /// <inheritdoc />
    public async Task<Result> ReleaseAsync(
        string passportCode,
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        var guard = GuardArgs(passportCode, year, month);
        if (guard.IsFailure)
        {
            return guard;
        }

        var row = await _db.VoucherQuotas
            .Where(q => q.PassportCode == passportCode && q.Year == year && q.IsActive)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(
                IVoucherQuotaService.QuotaNotConfiguredCode,
                $"No voucher quota configured for passport '{passportCode}' year {year}.");
        }

        // Underflow protection — releasing more than reserved is a programmer
        // error and must not silently roll the counter into the negative space.
        var sameMonthSnapshot = row.UsedMonth == month;
        if ((sameMonthSnapshot && row.UsedThisMonth <= 0) || row.UsedThisYear <= 0)
        {
            return Result.Failure(
                IVoucherQuotaService.QuotaUnderflowCode,
                $"Cannot release a voucher slot below zero for passport '{passportCode}' year {year} month {month}.");
        }

        if (sameMonthSnapshot)
        {
            row.UsedThisMonth -= 1;
        }
        row.UsedThisYear -= 1;
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        row.UpdatedAtUtc = now;
        row.UpdatedBy = actor;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IVoucherQuotaService.AuditReleased,
            AuditSeverity.Sensitive,
            actor,
            row.Id,
            new
            {
                passportCode,
                year,
                month,
                usedThisMonth = row.UsedThisMonth,
                usedThisYear = row.UsedThisYear,
            },
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<VoucherQuotaDto>> ConfigureQuotaAsync(
        string passportCode,
        int year,
        int monthlyQuota,
        int annualQuota,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(passportCode))
        {
            return Result<VoucherQuotaDto>.Failure(ErrorCodes.ValidationFailed, "PassportCode is required.");
        }
        if (year < 2000 || year > 2999)
        {
            return Result<VoucherQuotaDto>.Failure(ErrorCodes.ValidationFailed, "Year must be in [2000, 2999].");
        }
        if (monthlyQuota < 0 || annualQuota < 0)
        {
            return Result<VoucherQuotaDto>.Failure(ErrorCodes.ValidationFailed, "Quotas must be ≥ 0.");
        }

        var row = await _db.VoucherQuotas
            .Where(q => q.PassportCode == passportCode && q.Year == year && q.IsActive)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = _clock.UtcNow;
        var actor = _caller.UserSqid ?? "admin";
        if (row is null)
        {
            row = new VoucherQuota
            {
                PassportCode = passportCode,
                Year = year,
                MonthlyQuota = monthlyQuota,
                AnnualQuota = annualQuota,
                UsedThisMonth = 0,
                UsedThisYear = 0,
                UsedMonth = 0,
                CreatedAtUtc = now,
                CreatedBy = actor,
                IsActive = true,
            };
            _db.VoucherQuotas.Add(row);
        }
        else
        {
            row.MonthlyQuota = monthlyQuota;
            row.AnnualQuota = annualQuota;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = actor;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await EmitAuditAsync(
            IVoucherQuotaService.AuditConfigured,
            AuditSeverity.Critical,
            actor,
            row.Id,
            new
            {
                passportCode,
                year,
                monthlyQuota,
                annualQuota,
            },
            cancellationToken).ConfigureAwait(false);

        var snapshot = ComputeMonthlyRemaining(row, row.UsedMonth);
        return Result<VoucherQuotaDto>.Success(new VoucherQuotaDto(
            Id: _sqids.Encode(row.Id),
            PassportCode: row.PassportCode,
            Year: row.Year,
            MonthlyQuota: row.MonthlyQuota,
            AnnualQuota: row.AnnualQuota,
            UsedThisMonth: row.UsedThisMonth,
            UsedThisYear: row.UsedThisYear,
            UsedMonth: row.UsedMonth,
            MonthlyRemaining: snapshot,
            AnnualRemaining: ComputeAnnualRemaining(row)));
    }

    /// <summary>Computes the monthly remaining slot count, treating <c>MonthlyQuota=0</c> as uncapped.</summary>
    /// <param name="row">Loaded quota row.</param>
    /// <param name="targetMonth">Month-of-year being queried; a rollover resets the counter to the full cap.</param>
    /// <returns>The remaining slot count; <see cref="int.MaxValue"/> when uncapped.</returns>
    private static int ComputeMonthlyRemaining(VoucherQuota row, int targetMonth)
    {
        if (row.MonthlyQuota == 0)
        {
            return int.MaxValue;
        }
        var used = row.UsedMonth == targetMonth ? row.UsedThisMonth : 0;
        return Math.Max(0, row.MonthlyQuota - used);
    }

    /// <summary>Computes the annual remaining slot count, treating <c>AnnualQuota=0</c> as uncapped.</summary>
    /// <param name="row">Loaded quota row.</param>
    /// <returns>The remaining slot count; <see cref="int.MaxValue"/> when uncapped.</returns>
    private static int ComputeAnnualRemaining(VoucherQuota row)
        => row.AnnualQuota == 0 ? int.MaxValue : Math.Max(0, row.AnnualQuota - row.UsedThisYear);

    /// <summary>Validates the natural-key inputs shared by every public mutator.</summary>
    /// <param name="passportCode">Passport code.</param>
    /// <param name="year">Calendar year.</param>
    /// <param name="month">Month-of-year (1..12).</param>
    /// <returns>Success when arguments are well-formed.</returns>
    private static Result GuardArgs(string passportCode, int year, int month)
    {
        if (string.IsNullOrWhiteSpace(passportCode))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "PassportCode is required.");
        }
        if (year < 2000 || year > 2999)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Year must be in [2000, 2999].");
        }
        if (month < 1 || month > 12)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Month must be in [1, 12].");
        }
        return Result.Success();
    }

    /// <summary>Writes a single audit row with a serialised details payload.</summary>
    /// <param name="eventCode">Stable event code.</param>
    /// <param name="severity">Audit severity.</param>
    /// <param name="actor">Audit-attribution string.</param>
    /// <param name="targetEntityId">Database id of the affected row.</param>
    /// <param name="details">Arbitrary anonymous object serialised to JSON.</param>
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
            nameof(VoucherQuota),
            targetEntityId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);
    }
}
