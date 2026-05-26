using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Benefits;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Benefits;

/// <summary>
/// R0517 / TOR CF 02.05 — implementation of
/// <see cref="IBenefitPaymentStatusService"/>. Loads <see cref="BenefitPayment"/>
/// rows via <see cref="IReadOnlyCnasDbContext"/> (R0026 read-replica routing),
/// applies the requested window + type filter, computes the rolling
/// 12-month-paid and 3-month-scheduled totals (independent of the requested
/// window), and emits a Sensitive audit row per call.
/// </summary>
public sealed class BenefitPaymentStatusService : IBenefitPaymentStatusService
{
    /// <summary>Audit event code emitted on every successful status read.</summary>
    public const string AuditEventCode = "BENEFIT_PAYMENT.READ";

    /// <summary>Permission required by <see cref="GetForSolicitantAsync"/>.</summary>
    public const string ReadAnyPermission = "BenefitPayment.ReadAny";

    /// <summary>
    /// Default size of the lookback portion of the window when the caller
    /// omits <c>FromMonth</c>. Matches the rolling-totals window so the
    /// citizen never sees a payment that contributes to the rolling total but
    /// is outside the visible list by default.
    /// </summary>
    public const int DefaultLookbackMonths = 12;

    /// <summary>
    /// Default size of the lookahead portion of the window when the caller
    /// omits <c>ToMonth</c>. Matches the "scheduled" rolling total for the
    /// same symmetry reason.
    /// </summary>
    public const int DefaultLookaheadMonths = 3;

    private readonly IReadOnlyCnasDbContext _db;
    private readonly ICnasDbContext _writeDb;
    private readonly IValidator<BenefitPaymentStatusQueryDto> _validator;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">Read-only EF Core context for the BenefitPayment lookup (R0026 routing).</param>
    /// <param name="writeDb">Write-side context used only to resolve the caller's UserProfile→Solicitant link (needs read-your-own-writes consistency).</param>
    /// <param name="validator">FluentValidation validator for the query envelope.</param>
    /// <param name="sqids">Sqid encoder used to render Solicitant + BenefitPayment ids on the output DTO.</param>
    /// <param name="caller">Per-request caller context — used to resolve the current user's Solicitant + permission check.</param>
    /// <param name="clock">UTC clock used to anchor the rolling window and stamp <see cref="BenefitPaymentStatusDto.GeneratedAtUtc"/>.</param>
    /// <param name="audit">Audit-log façade.</param>
    public BenefitPaymentStatusService(
        IReadOnlyCnasDbContext db,
        ICnasDbContext writeDb,
        IValidator<BenefitPaymentStatusQueryDto> validator,
        ISqidService sqids,
        ICallerContext caller,
        ICnasTimeProvider clock,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(writeDb);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _writeDb = writeDb;
        _validator = validator;
        _sqids = sqids;
        _caller = caller;
        _clock = clock;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<BenefitPaymentStatusDto>> GetForCurrentUserAsync(
        BenefitPaymentStatusQueryDto query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 1. Defense in depth — the controller carries [Authorize], but
        //    internal callers could bypass it.
        if (_caller.UserId is not long userId)
        {
            return Result<BenefitPaymentStatusDto>.Failure(
                ErrorCodes.Unauthorized,
                "Benefit-payment status requires an authenticated caller.");
        }

        // 2. Validate the query envelope before touching any business logic.
        var validation = await _validator.ValidateAsync(query, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            var first = validation.Errors[0];
            return Result<BenefitPaymentStatusDto>.Failure(
                ErrorCodes.ValidationFailed,
                first.ErrorMessage);
        }

        // 3. Resolve the caller's Solicitant via the canonical
        //    UserProfile→Solicitant identity link. Mirrors
        //    PersonalAccountExtractService for the same problem.
        var nationalIdHash = await _writeDb.UserProfiles
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.NationalIdHash)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(nationalIdHash))
        {
            return Result<BenefitPaymentStatusDto>.Failure(
                ErrorCodes.NotFound,
                "No Solicitant is linked to the calling user.");
        }

        var solicitantId = await _writeDb.Solicitants
            .Where(s => s.NationalIdHash == nationalIdHash && s.IsActive)
            .Select(s => (long?)s.Id)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (solicitantId is null)
        {
            return Result<BenefitPaymentStatusDto>.Failure(
                ErrorCodes.NotFound,
                "No Solicitant is linked to the calling user.");
        }

        return await BuildStatusAsync(solicitantId.Value, query, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<BenefitPaymentStatusDto>> GetForSolicitantAsync(
        long solicitantId,
        BenefitPaymentStatusQueryDto query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Permission gate — only callers carrying the explicit ReadAny
        // permission may pull arbitrary citizen status payloads.
        if (!_caller.Roles.Contains(ReadAnyPermission, StringComparer.Ordinal))
        {
            return Result<BenefitPaymentStatusDto>.Failure(
                ErrorCodes.Forbidden,
                $"Permission '{ReadAnyPermission}' is required to read arbitrary benefit-payment status.");
        }

        // Validate the query envelope before issuing the query.
        var validation = await _validator.ValidateAsync(query, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            var first = validation.Errors[0];
            return Result<BenefitPaymentStatusDto>.Failure(
                ErrorCodes.ValidationFailed,
                first.ErrorMessage);
        }

        return await BuildStatusAsync(solicitantId, query, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the benefit-payment rows for <paramref name="solicitantId"/>
    /// inside the requested (or default) window, computes the rolling
    /// totals, writes the audit row, and returns the populated DTO.
    /// </summary>
    /// <param name="solicitantId">Raw bigint id of the target Solicitant.</param>
    /// <param name="query">Validated query envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Populated status DTO.</returns>
    private async Task<Result<BenefitPaymentStatusDto>> BuildStatusAsync(
        long solicitantId,
        BenefitPaymentStatusQueryDto query,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);

        // Resolve effective window — caller-supplied bounds win, otherwise
        // substitute the default lookback / lookahead anchored at the first
        // of the current month.
        var fromMonth = NormaliseToFirstOfMonth(
            query.FromMonth ?? firstOfThisMonth.AddMonths(-DefaultLookbackMonths));
        var toMonth = NormaliseToFirstOfMonth(
            query.ToMonth ?? firstOfThisMonth.AddMonths(DefaultLookaheadMonths));

        // Resolve optional type filter — validated above, so the parse cannot
        // fail when query.Type is non-null.
        BenefitType? typeFilter = null;
        if (!string.IsNullOrEmpty(query.Type))
        {
            typeFilter = Enum.Parse<BenefitType>(query.Type, ignoreCase: false);
        }

        // Pull all matching rows in a single round-trip. Volumes are bounded
        // by the 36-month window cap; in-memory aggregation keeps the LINQ
        // tree friendly to the InMemory test provider.
        var rows = await _db.BenefitPayments
            .Where(p => p.BeneficiarySolicitantId == solicitantId
                        && p.IsActive
                        && p.PaymentMonth >= fromMonth
                        && p.PaymentMonth <= toMonth
                        && (typeFilter == null || p.BenefitType == typeFilter.Value))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Rolling totals are independent of the requested window — anchor
        // against the server clock so successive calls produce comparable
        // totals regardless of the caller-supplied bounds. We pull a second
        // narrow slice to compute the totals; the row volume stays bounded
        // because the rolling windows are at most 12 + 3 = 15 months.
        var paidFrom = firstOfThisMonth.AddMonths(-DefaultLookbackMonths);
        var paidTo = firstOfThisMonth;
        var scheduledFrom = firstOfThisMonth;
        var scheduledTo = firstOfThisMonth.AddMonths(DefaultLookaheadMonths);

        var totalPaidLast12Months = await _db.BenefitPayments
            .Where(p => p.BeneficiarySolicitantId == solicitantId
                        && p.IsActive
                        && p.Status == BenefitPaymentStatus.Paid
                        && p.PaymentMonth >= paidFrom
                        && p.PaymentMonth <= paidTo)
            .SumAsync(p => (decimal?)p.NetAmount, ct)
            .ConfigureAwait(false) ?? 0m;

        var totalScheduledNext3Months = await _db.BenefitPayments
            .Where(p => p.BeneficiarySolicitantId == solicitantId
                        && p.IsActive
                        && p.Status == BenefitPaymentStatus.Scheduled
                        && p.PaymentMonth >= scheduledFrom
                        && p.PaymentMonth <= scheduledTo)
            .SumAsync(p => (decimal?)p.NetAmount, ct)
            .ConfigureAwait(false) ?? 0m;

        // Sort the row list by PaymentMonth DESC then by BenefitType so the
        // wire shape is deterministic.
        var sortedRows = rows
            .OrderByDescending(p => p.PaymentMonth)
            .ThenBy(p => p.BenefitType)
            .Select(p => new BenefitPaymentDto(
                Id: _sqids.Encode(p.Id),
                BenefitType: p.BenefitType.ToString(),
                PaymentMonth: p.PaymentMonth,
                GrossAmount: p.GrossAmount,
                NetAmount: p.NetAmount,
                TaxWithheld: p.TaxWithheld,
                Status: p.Status.ToString(),
                Method: p.Method.ToString(),
                BankAccountIban: p.BankAccountIban,
                PostalOrderNumber: p.PostalOrderNumber,
                IssuedDate: p.IssuedDate,
                PaidDate: p.PaidDate,
                ReturnedDate: p.ReturnedDate,
                ReturnReason: p.ReturnReason))
            .ToList();

        var solicitantSqid = _sqids.Encode(solicitantId);
        var dto = new BenefitPaymentStatusDto(
            SolicitantSqid: solicitantSqid,
            Payments: sortedRows,
            TotalPaidLast12Months: totalPaidLast12Months,
            TotalScheduledNext3Months: totalScheduledNext3Months,
            GeneratedAtUtc: now);

        // Audit Sensitive — access to citizen financial-disbursement data.
        var details = JsonSerializer.Serialize(new
        {
            solicitantSqid,
            monthsReturned = sortedRows.Count,
            totalPaid = totalPaidLast12Months.ToString("F2", CultureInfo.InvariantCulture),
        });
        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Sensitive,
            actorId: _caller.UserSqid ?? "anonymous",
            targetEntity: nameof(BenefitPayment),
            targetEntityId: solicitantId,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        return Result<BenefitPaymentStatusDto>.Success(dto);
    }

    /// <summary>
    /// Normalises a <see cref="DateOnly"/> to the first day of its month —
    /// every BenefitPayment row stores its <c>PaymentMonth</c> as day=1, so
    /// window filters must match the same convention.
    /// </summary>
    /// <param name="d">Any date in the target month.</param>
    /// <returns>The first day of the supplied month.</returns>
    private static DateOnly NormaliseToFirstOfMonth(DateOnly d)
        => new(d.Year, d.Month, 1);
}
