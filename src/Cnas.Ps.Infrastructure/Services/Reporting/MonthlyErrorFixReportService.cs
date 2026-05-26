using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Reporting;

/// <summary>
/// R2462 / Deliverable 7.2 — concrete <see cref="IMonthlyErrorFixReportService"/>.
/// Aggregates monthly error-fix + documentation-update metrics from
/// <c>IntegrityCheckFinding</c>, <c>ChangeRequest</c>, and
/// <c>TemplateVariant</c> rows against the read-replica seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure-read.</b> Carries <see cref="LongRunningReportServiceAttribute"/> so
/// the architecture suite pins the read-replica-only routing (R1904 / ARH 025).
/// No audit emission, no DbContext writes.
/// </para>
/// <para>
/// <b>Bucketing rules.</b>
/// <list type="bullet">
///   <item>Integrity findings count when their <c>FirstDetectedAt</c> falls in the month.</item>
///   <item>Change requests "rolled back" count when their <c>RolledBackAt</c> falls in the month.</item>
///   <item>Change requests "deployed" count when their <c>DeployedAt</c> falls in the month.</item>
///   <item>Template variant updates count when (<c>UpdatedAtUtc</c> in month)
///         OR (<c>UpdatedAtUtc IS NULL</c> AND <c>CreatedAtUtc</c> in month) —
///         i.e. each row whose latest write landed inside the window.</item>
/// </list>
/// </para>
/// </remarks>
[LongRunningReportService]
public sealed class MonthlyErrorFixReportService : IMonthlyErrorFixReportService
{
    private readonly IReadOnlyCnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly IValidator<MonthlyErrorFixReportInputDto> _validator;

    /// <summary>Constructs the service.</summary>
    /// <param name="db">Read-replica EF Core seam (R0026 / ARH 025).</param>
    /// <param name="clock">UTC time provider (CLAUDE.md RULE 4).</param>
    /// <param name="validator">FluentValidation guard for the input envelope.</param>
    /// <exception cref="ArgumentNullException">When any collaborator is null.</exception>
    public MonthlyErrorFixReportService(
        IReadOnlyCnasDbContext db,
        ICnasTimeProvider clock,
        IValidator<MonthlyErrorFixReportInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(validator);
        _db = db;
        _clock = clock;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<MonthlyErrorFixReportDto>> ComputeAsync(
        MonthlyErrorFixReportInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Validate the input.
        var validation = await _validator.ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            var msg = string.Join("; ", validation.Errors.Select(e => e.ErrorMessage));
            return Result<MonthlyErrorFixReportDto>.Failure(ErrorCodes.ValidationFailed, msg);
        }

        // 2. Compute the [start, end) UTC window.
        var start = new DateTime(input.Month.Year, input.Month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);

        // 3. Integrity findings: pull rows whose FirstDetectedAt is in the
        //    window. We materialise just the two columns we need so the
        //    in-memory aggregation is small.
        var findings = await _db.IntegrityCheckFindings
            .Where(f => f.FirstDetectedAt >= start && f.FirstDetectedAt < end)
            .Select(f => new { f.Severity, f.AggregateName })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var totalIntegrity = findings.Count;
        var critical = findings.Count(f => f.Severity == IntegrityFindingSeverity.Critical);
        var high = findings.Count(f => f.Severity == IntegrityFindingSeverity.High);
        var medium = findings.Count(f => f.Severity == IntegrityFindingSeverity.Medium);
        var low = findings.Count(f => f.Severity == IntegrityFindingSeverity.Low);

        var categoryBreakdown = findings
            .GroupBy(f => new { f.AggregateName, f.Severity })
            .OrderBy(g => g.Key.AggregateName, StringComparer.Ordinal)
            .ThenBy(g => (int)g.Key.Severity)
            .Select(g => new MonthlyErrorFixCategoryBreakdownRow(
                AggregateName: g.Key.AggregateName,
                Severity: g.Key.Severity.ToString(),
                Count: g.Count()))
            .ToList();

        // 4. ChangeRequest counts — rolled-back vs deployed in the window.
        //    Use the nullable timestamps; rows whose timestamp is null are
        //    not in the bucket (matches the "in the month" intent of R2462).
        var totalRolledBack = await _db.ChangeRequests
            .Where(c => c.RolledBackAt != null && c.RolledBackAt >= start && c.RolledBackAt < end)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        var totalDeployed = await _db.ChangeRequests
            .Where(c => c.DeployedAt != null && c.DeployedAt >= start && c.DeployedAt < end)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        // 5. Template variant updates — count rows whose latest write lands in
        //    the window. We treat a row that was never updated (UpdatedAtUtc
        //    null) as "modified at CreatedAtUtc" so the bucket captures both
        //    new variants and edits.
        var totalTemplateUpdates = await _db.TemplateVariants
            .Where(v =>
                (v.UpdatedAtUtc != null && v.UpdatedAtUtc >= start && v.UpdatedAtUtc < end)
                || (v.UpdatedAtUtc == null && v.CreatedAtUtc >= start && v.CreatedAtUtc < end))
            .CountAsync(cancellationToken).ConfigureAwait(false);

        var dto = new MonthlyErrorFixReportDto(
            Month: input.Month,
            GeneratedAtUtc: _clock.UtcNow,
            TotalIntegrityFindings: totalIntegrity,
            IntegrityFindingsByCriticalSeverity: critical,
            IntegrityFindingsByHighSeverity: high,
            IntegrityFindingsByMediumSeverity: medium,
            IntegrityFindingsByLowSeverity: low,
            TotalChangeRequestsRolledBack: totalRolledBack,
            TotalChangeRequestsDeployed: totalDeployed,
            TotalDocumentationTemplatesUpdated: totalTemplateUpdates,
            CategoryBreakdown: categoryBreakdown);
        return Result<MonthlyErrorFixReportDto>.Success(dto);
    }
}
