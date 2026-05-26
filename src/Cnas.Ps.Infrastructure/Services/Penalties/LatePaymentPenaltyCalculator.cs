using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Penalties;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services.Penalties;

/// <summary>
/// R0819 / TOR BP 1.2-J — concrete implementation of
/// <see cref="ILatePaymentPenaltyCalculator"/>. Computes a per-day late
/// penalty on the unpaid principal of an overdue
/// <see cref="MonthlyContributionCalculation"/> row. Operation is idempotent
/// on the (contributor, month, up-to-date) natural key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pragmatic principal shortcut.</b> Today the calculator copies
/// <see cref="MonthlyContributionCalculation.TotalAdjusted"/> verbatim into
/// <see cref="LatePaymentPenalty.PrincipalAmount"/> because no payments
/// ledger exists yet. Once R0814 / R0818 land the principal will be reduced
/// by the sum of receipts attributed to the month.
/// </para>
/// </remarks>
public sealed class LatePaymentPenaltyCalculator : ILatePaymentPenaltyCalculator
{
    /// <summary>Stable audit event code emitted on every successful calculation.</summary>
    public const string AuditCalculated = "LATE_PENALTY.CALCULATED";

    /// <summary>Stable audit event code emitted on a successful waive.</summary>
    public const string AuditWaived = "LATE_PENALTY.WAIVED";

    /// <summary>Stable outcome tag value for successful calculations.</summary>
    public const string OutcomeSucceeded = "succeeded";

    /// <summary>Stable outcome tag value for failed calculations.</summary>
    public const string OutcomeFailed = "failed";

    /// <summary>Stable failure message attached when no monthly roll-up exists.</summary>
    public const string MonthlyCalcNotFoundMessage = "MONTHLY_CALC_NOT_FOUND";

    /// <summary>Cached JSON serializer options shared across audit-payload builders.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _readDb;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly PenaltyOptions _options;
    private readonly IValidator<LatePaymentPenaltyCalculateInputDto> _calcValidator;
    private readonly IValidator<LatePaymentPenaltyWaiveInputDto> _waiveValidator;

    /// <summary>Constructs the calculator with its collaborators.</summary>
    /// <param name="db">Write-side DbContext used to upsert the penalty row.</param>
    /// <param name="readDb">Read-only DbContext used to look up the monthly roll-up (R0026 routing).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder for the outbound DTO.</param>
    /// <param name="caller">Caller context for audit attribution.</param>
    /// <param name="audit">Audit-log façade.</param>
    /// <param name="options">Penalty configuration (daily rate + due-date convention).</param>
    /// <param name="calcValidator">Validator for the calculate-input shape.</param>
    /// <param name="waiveValidator">Validator for the waive-input shape.</param>
    public LatePaymentPenaltyCalculator(
        ICnasDbContext db,
        IReadOnlyCnasDbContext readDb,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IOptions<PenaltyOptions> options,
        IValidator<LatePaymentPenaltyCalculateInputDto> calcValidator,
        IValidator<LatePaymentPenaltyWaiveInputDto> waiveValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(calcValidator);
        ArgumentNullException.ThrowIfNull(waiveValidator);
        _db = db;
        _readDb = readDb;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _options = options.Value;
        _calcValidator = calcValidator;
        _waiveValidator = waiveValidator;
    }

    /// <inheritdoc />
    public async Task<Result<LatePaymentPenaltyDto>> CalculateAsync(
        long contributorId,
        DateOnly month,
        DateOnly upToDate,
        CancellationToken ct = default)
    {
        var input = new LatePaymentPenaltyCalculateInputDto(month, upToDate);
        var validation = await _calcValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            CnasMeter.LatePenaltyCalculated.Add(1,
                new KeyValuePair<string, object?>("outcome", OutcomeFailed));
            return Result<LatePaymentPenaltyDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        // Load the monthly roll-up — penalty cannot be computed without it.
        var monthly = await _readDb.MonthlyContributionCalculations
            .Where(r => r.ContributorId == contributorId && r.Month == month && r.IsActive)
            .Select(r => new { r.TotalAdjusted })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (monthly is null)
        {
            CnasMeter.LatePenaltyCalculated.Add(1,
                new KeyValuePair<string, object?>("outcome", OutcomeFailed));
            return Result<LatePaymentPenaltyDto>.Failure(
                ErrorCodes.NotFound,
                MonthlyCalcNotFoundMessage);
        }

        // Pragmatic shortcut — until a payments ledger exists treat the
        // entire adjusted total as unpaid principal.
        var principal = monthly.TotalAdjusted;
        var dueDate = ComputeDueDate(month, _options.DueDateOfMonthFollowing);
        var daysLate = upToDate > dueDate ? upToDate.DayNumber - dueDate.DayNumber : 0;
        var dailyRate = _options.DailyRatePercent;
        // round(principal × rate/100 × days, 2) — MidpointRounding.ToEven matches
        // .NET decimal default which is the regulator-expected behaviour.
        var penalty = decimal.Round(
            principal * (dailyRate / 100m) * daysLate,
            2,
            MidpointRounding.ToEven);

        // Idempotent upsert on (ContributorId, Month, UpToDate).
        var existing = await _db.LatePaymentPenalties
            .SingleOrDefaultAsync(
                r => r.ContributorId == contributorId &&
                     r.Month == month &&
                     r.UpToDate == upToDate,
                ct)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        LatePaymentPenalty entity;
        if (existing is null)
        {
            entity = new LatePaymentPenalty
            {
                ContributorId = contributorId,
                Month = month,
                PrincipalAmount = principal,
                CalculatedAtUtc = now,
                DueDate = dueDate,
                UpToDate = upToDate,
                DaysLate = daysLate,
                DailyRatePercent = dailyRate,
                PenaltyAmount = penalty,
                IsWaived = false,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.LatePaymentPenalties.Add(entity);
        }
        else
        {
            existing.PrincipalAmount = principal;
            existing.CalculatedAtUtc = now;
            existing.DueDate = dueDate;
            existing.DaysLate = daysLate;
            existing.DailyRatePercent = dailyRate;
            existing.PenaltyAmount = penalty;
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = _caller.UserSqid;
            existing.IsActive = true;
            entity = existing;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                contributorSqid = _sqids.Encode(contributorId),
                month = month.ToString("O", CultureInfo.InvariantCulture),
                upToDate = upToDate.ToString("O", CultureInfo.InvariantCulture),
                principal,
                daysLate,
                dailyRate,
                penalty,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCalculated,
            AuditSeverity.Information,
            _caller.UserSqid ?? "?",
            nameof(LatePaymentPenalty),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.LatePenaltyCalculated.Add(1,
            new KeyValuePair<string, object?>("outcome", OutcomeSucceeded));

        return Result<LatePaymentPenaltyDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result> WaiveAsync(long penaltyId, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var validation = await _waiveValidator
            .ValidateAsync(new LatePaymentPenaltyWaiveInputDto(reason), ct)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var entity = await _db.LatePaymentPenalties
            .SingleOrDefaultAsync(r => r.Id == penaltyId && r.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Late penalty not found.");
        }
        if (entity.IsWaived)
        {
            return Result.Failure(ErrorCodes.Conflict, "Penalty is already waived.");
        }

        var now = _clock.UtcNow;
        entity.IsWaived = true;
        entity.WaiveReason = reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                penaltySqid = _sqids.Encode(entity.Id),
                contributorSqid = _sqids.Encode(entity.ContributorId),
                month = entity.Month.ToString("O", CultureInfo.InvariantCulture),
                upToDate = entity.UpToDate.ToString("O", CultureInfo.InvariantCulture),
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditWaived,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(LatePaymentPenalty),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Computes the statutory due date for the supplied reporting month —
    /// the <paramref name="dueDayOfMonthFollowing"/>-th day of the month
    /// following <paramref name="month"/>.
    /// </summary>
    /// <param name="month">Reporting month (day = 1 by convention).</param>
    /// <param name="dueDayOfMonthFollowing">Day-of-month for the deadline (1..28).</param>
    /// <returns>The computed due date.</returns>
    private static DateOnly ComputeDueDate(DateOnly month, int dueDayOfMonthFollowing)
    {
        var nextMonth = month.AddMonths(1);
        // Clamp the configured day to the actual month length so a January 31
        // configuration on a February 28-day month does not throw.
        var daysInMonth = DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month);
        var day = Math.Min(dueDayOfMonthFollowing, daysInMonth);
        return new DateOnly(nextMonth.Year, nextMonth.Month, day);
    }

    /// <summary>Projects a <see cref="LatePaymentPenalty"/> entity into its outbound DTO.</summary>
    /// <param name="entity">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private LatePaymentPenaltyDto ToDto(LatePaymentPenalty entity) => new(
        Id: _sqids.Encode(entity.Id),
        ContributorSqid: _sqids.Encode(entity.ContributorId),
        Month: entity.Month,
        PrincipalAmount: entity.PrincipalAmount,
        CalculatedAtUtc: entity.CalculatedAtUtc,
        DueDate: entity.DueDate,
        UpToDate: entity.UpToDate,
        DaysLate: entity.DaysLate,
        DailyRatePercent: entity.DailyRatePercent,
        PenaltyAmount: entity.PenaltyAmount,
        IsWaived: entity.IsWaived,
        WaiveReason: entity.WaiveReason);
}
