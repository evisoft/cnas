using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Declarations;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Declarations;

/// <summary>
/// R0813 / TOR BP 1.2-D — concrete implementation of
/// <see cref="IMonthlyContributionCalculator"/>. Aggregates every
/// non-cancelled <see cref="Declaration"/> for a (contributor, month) tuple
/// into a single <see cref="MonthlyContributionCalculation"/> row. The
/// operation is idempotent on the natural key.
/// </summary>
public sealed class MonthlyContributionCalculator : IMonthlyContributionCalculator
{
    /// <summary>Stable audit event code emitted on every successful calculation.</summary>
    public const string AuditCompleted = "CONTRIBUTOR.MONTHLY_CALC.COMPLETED";

    /// <summary>Stable outcome tag value for successful calculations.</summary>
    public const string OutcomeSucceeded = "succeeded";

    /// <summary>Stable outcome tag value for failed calculations (validation / not-found).</summary>
    public const string OutcomeFailed = "failed";

    /// <summary>
    /// Cached JSON serializer options shared across audit-payload builders to
    /// satisfy CA1869.
    /// </summary>
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

    /// <summary>Constructs the calculator with its collaborators.</summary>
    /// <param name="db">Write-side DbContext used to upsert the calculation row.</param>
    /// <param name="readDb">Read-only DbContext used for the aggregation query (R0026 routing).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder for the outbound DTO.</param>
    /// <param name="caller">Caller context for audit attribution.</param>
    /// <param name="audit">Audit-log façade.</param>
    public MonthlyContributionCalculator(
        ICnasDbContext db,
        IReadOnlyCnasDbContext readDb,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(readDb);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _readDb = readDb;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<MonthlyContributionCalculationDto>> CalculateAsync(
        long contributorId,
        DateOnly month,
        CancellationToken ct = default)
    {
        if (month.Day != 1)
        {
            CnasMeter.ContributorMonthlyCalc.Add(1,
                new KeyValuePair<string, object?>("outcome", OutcomeFailed));
            return Result<MonthlyContributionCalculationDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Month must be the first day of the month (Day == 1).");
        }

        var contributorExists = await _db.Contributors
            .AnyAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (!contributorExists)
        {
            CnasMeter.ContributorMonthlyCalc.Add(1,
                new KeyValuePair<string, object?>("outcome", OutcomeFailed));
            return Result<MonthlyContributionCalculationDto>.Failure(
                ErrorCodes.NotFound,
                "Contributor not found.");
        }

        // R0813 — pull every non-cancelled declaration for the (contributor,
        // month) tuple. The aggregation is done in-process so the test fixture
        // (InMemory) and the production path share the exact same arithmetic.
        // Reads use the read-only context per the R0026 routing convention.
        var declarations = await _readDb.Declarations
            .Where(d =>
                d.ContributorId == contributorId &&
                d.IsActive &&
                d.ReportingMonth == month &&
                d.Status != DeclarationStatus.Cancelled)
            .Select(d => new { d.DeclaredContributionAmount, d.AdjustedContributionAmount })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        decimal totalDeclared = 0m;
        decimal totalAdjusted = 0m;
        foreach (var row in declarations)
        {
            totalDeclared += row.DeclaredContributionAmount;
            totalAdjusted += row.AdjustedContributionAmount ?? row.DeclaredContributionAmount;
        }
        var count = declarations.Count;
        var delta = totalAdjusted - totalDeclared;
        decimal? overpayment = delta < 0m ? -delta : null;
        decimal? underpayment = delta > 0m ? delta : null;

        // Idempotent upsert — load any existing row for the natural key and
        // update in place, otherwise insert a new row.
        var existing = await _db.MonthlyContributionCalculations
            .SingleOrDefaultAsync(r => r.ContributorId == contributorId && r.Month == month, ct)
            .ConfigureAwait(false);
        var now = _clock.UtcNow;
        MonthlyContributionCalculation entity;
        if (existing is null)
        {
            entity = new MonthlyContributionCalculation
            {
                ContributorId = contributorId,
                Month = month,
                TotalDeclared = totalDeclared,
                TotalAdjusted = totalAdjusted,
                OverpaymentAmount = overpayment,
                UnderpaymentAmount = underpayment,
                DeclarationCount = count,
                CalculatedAtUtc = now,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.MonthlyContributionCalculations.Add(entity);
        }
        else
        {
            existing.TotalDeclared = totalDeclared;
            existing.TotalAdjusted = totalAdjusted;
            existing.OverpaymentAmount = overpayment;
            existing.UnderpaymentAmount = underpayment;
            existing.DeclarationCount = count;
            existing.CalculatedAtUtc = now;
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = _caller.UserSqid;
            // Reactivate if previously soft-deleted (defensive).
            existing.IsActive = true;
            entity = existing;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                contributorSqid = _sqids.Encode(contributorId),
                month = month.ToString("O", CultureInfo.InvariantCulture),
                totalDeclared,
                totalAdjusted,
                overpayment,
                underpayment,
                count,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCompleted,
            AuditSeverity.Information,
            _caller.UserSqid ?? "?",
            nameof(MonthlyContributionCalculation),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ContributorMonthlyCalc.Add(1,
            new KeyValuePair<string, object?>("outcome", OutcomeSucceeded));

        var dto = new MonthlyContributionCalculationDto(
            Id: _sqids.Encode(entity.Id),
            ContributorSqid: _sqids.Encode(contributorId),
            Month: month,
            TotalDeclared: totalDeclared,
            TotalAdjusted: totalAdjusted,
            OverpaymentAmount: overpayment,
            UnderpaymentAmount: underpayment,
            DeclarationCount: count,
            CalculatedAtUtc: now);
        return Result<MonthlyContributionCalculationDto>.Success(dto);
    }
}
