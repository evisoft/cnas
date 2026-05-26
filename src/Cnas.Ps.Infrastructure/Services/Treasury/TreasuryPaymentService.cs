using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Treasury;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Treasury;

/// <summary>
/// R0911 / TOR BP 2.2-B — concrete implementation of
/// <see cref="ITreasuryPaymentService"/>. Owns the receipt-import path
/// (single receipt) and the per-receipt distribution path that projects
/// matching REV-5 rows into <see cref="PersonalAccountEntry"/>.
/// </summary>
public sealed class TreasuryPaymentService : ITreasuryPaymentService
{
    /// <summary>Stable audit event code emitted on a successful import.</summary>
    public const string AuditImported = "TREASURY.PAYMENT_IMPORTED";

    /// <summary>Stable audit event code emitted on a successful distribution.</summary>
    public const string AuditDistributed = "TREASURY.PAYMENT_DISTRIBUTED";

    /// <summary>Stable source-code stamped on every <see cref="PersonalAccountEntry"/> projected from a Treasury receipt.</summary>
    public const string PersonalAccountSourceCode = "TREASURY";

    /// <summary>Stable failure message used when the natural-key index rejects the insert.</summary>
    public const string DuplicateMessage = "DUPLICATE_TREASURY_REFERENCE";

    /// <summary>Stable failure message used when no REV-5 rows match the receipt's (payer × month) tuple.</summary>
    public const string NoRev5ToDistributeMessage = "NO_REV5_TO_DISTRIBUTE";

    /// <summary>Stable failure message returned when a receipt has already been distributed.</summary>
    public const string AlreadyDistributedMessage = "ALREADY_DISTRIBUTED";

    /// <summary>Cached JSON serializer options shared across audit-payload builders.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IValidator<TreasuryPaymentReceiptImportInputDto> _importValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="importValidator">Validator for the import-input shape.</param>
    public TreasuryPaymentService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<TreasuryPaymentReceiptImportInputDto> importValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(importValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _importValidator = importValidator;
    }

    /// <inheritdoc />
    public async Task<Result<TreasuryPaymentReceiptDto>> ImportReceiptAsync(
        TreasuryPaymentReceiptImportInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _importValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<TreasuryPaymentReceiptDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.PayerContributorSqid);
        if (decoded.IsFailure)
        {
            return Result<TreasuryPaymentReceiptDto>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var payerId = decoded.Value;

        // Defensive payer-existence check — the row carries no navigation
        // property so a bogus PayerContributorId would persist dangling rows.
        var payerExists = await _db.Contributors
            .AnyAsync(c => c.Id == payerId && c.IsActive, ct).ConfigureAwait(false);
        if (!payerExists)
        {
            return Result<TreasuryPaymentReceiptDto>.Failure(
                ErrorCodes.NotFound, "Payer Contributor not found.");
        }

        // Natural-key duplicate probe — surface a stable Conflict rather than
        // relying on the database to throw (InMemory test provider has no
        // unique-index enforcement).
        var duplicate = await _db.TreasuryPaymentReceipts
            .AnyAsync(r => r.TreasuryReferenceNumber == input.TreasuryReferenceNumber && r.IsActive, ct)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Result<TreasuryPaymentReceiptDto>.Failure(
                ErrorCodes.ValidationFailed, DuplicateMessage);
        }

        var now = _clock.UtcNow;
        var receipt = new TreasuryPaymentReceipt
        {
            TreasuryReferenceNumber = input.TreasuryReferenceNumber,
            ReceiptDate = input.ReceiptDate,
            PayerContributorId = payerId,
            ReportingMonth = input.ReportingMonth,
            AmountReceived = input.AmountReceived,
            DistributionStatus = TreasuryPaymentDistributionStatus.Pending,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.TreasuryPaymentReceipts.Add(receipt);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            receiptSqid = _sqids.Encode(receipt.Id),
            payerSqid = input.PayerContributorSqid,
            treasuryReferenceNumber = input.TreasuryReferenceNumber,
            reportingMonth = input.ReportingMonth.ToString("O", CultureInfo.InvariantCulture),
            input.AmountReceived,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditImported,
            AuditSeverity.Information,
            _caller.UserSqid ?? "?",
            nameof(TreasuryPaymentReceipt),
            receipt.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<TreasuryPaymentReceiptDto>.Success(ToDto(receipt));
    }

    /// <inheritdoc />
    public async Task<Result<TreasuryPaymentReceiptDto>> DistributeAsync(
        long receiptId,
        CancellationToken ct = default)
    {
        var receipt = await _db.TreasuryPaymentReceipts
            .SingleOrDefaultAsync(r => r.Id == receiptId && r.IsActive, ct).ConfigureAwait(false);
        if (receipt is null)
        {
            return Result<TreasuryPaymentReceiptDto>.Failure(
                ErrorCodes.NotFound, "Treasury payment receipt not found.");
        }
        if (receipt.DistributionStatus != TreasuryPaymentDistributionStatus.Pending)
        {
            return Result<TreasuryPaymentReceiptDto>.Failure(
                ErrorCodes.ValidationFailed, AlreadyDistributedMessage);
        }

        var now = _clock.UtcNow;
        var year = receipt.ReportingMonth.Year;
        var month = receipt.ReportingMonth.Month;

        // Find matching REV-5 rows: rows whose parent header is filed by the
        // payer for the receipt's reporting month, excluding cancelled
        // headers. Each REV-5 row carries the IDNP hash + ContributionAmount
        // we need to weight the distribution.
        var matchingRows = await (
            from row in _db.Rev5DeclarationRows
            join header in _db.Rev5Declarations on row.Rev5DeclarationId equals header.Id
            where row.IsActive && header.IsActive
                && header.FilingContributorId == receipt.PayerContributorId
                && header.ReportingMonth == receipt.ReportingMonth
                && header.Status != Rev5DeclarationStatus.Cancelled
            select row).ToListAsync(ct).ConfigureAwait(false);

        // No matching REV-5 — entire amount is undistributed remainder.
        if (matchingRows.Count == 0)
        {
            receipt.DistributionStatus = TreasuryPaymentDistributionStatus.Failed;
            receipt.DistributionFailureReason = NoRev5ToDistributeMessage;
            receipt.UndistributedRemainderAmount = receipt.AmountReceived;
            receipt.DistributedAtUtc = now;
            receipt.UpdatedAtUtc = now;
            receipt.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await RecordDistributionAuditAsync(receipt, distributedToCount: 0, ct).ConfigureAwait(false);
            CnasMeter.TreasuryDistributed.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            return Result<TreasuryPaymentReceiptDto>.Success(ToDto(receipt));
        }

        // Compute the total declared base (sum of ContributionAmount across
        // matched rows) — the weight used in proportional distribution.
        decimal totalDeclared = 0m;
        foreach (var row in matchingRows)
        {
            totalDeclared += row.ContributionAmount;
        }
        if (totalDeclared <= 0m)
        {
            // Pathological — matched rows all carry zero amounts. Treat as
            // no-match and surface the entire amount as remainder.
            receipt.DistributionStatus = TreasuryPaymentDistributionStatus.Failed;
            receipt.DistributionFailureReason = NoRev5ToDistributeMessage;
            receipt.UndistributedRemainderAmount = receipt.AmountReceived;
            receipt.DistributedAtUtc = now;
            receipt.UpdatedAtUtc = now;
            receipt.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await RecordDistributionAuditAsync(receipt, distributedToCount: 0, ct).ConfigureAwait(false);
            CnasMeter.TreasuryDistributed.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            return Result<TreasuryPaymentReceiptDto>.Success(ToDto(receipt));
        }

        // Walk each matching row and proportionally credit the personal
        // account. Track the actually-distributed sum so we can surface the
        // residual (rows whose Solicitant or personal account is missing) as
        // the undistributed remainder.
        decimal actuallyDistributed = 0m;
        int distributedToCount = 0;
        int unresolvedCount = 0;
        foreach (var row in matchingRows)
        {
            // Proportional share weighted by the row's ContributionAmount.
            var share = decimal.Round(
                receipt.AmountReceived * row.ContributionAmount / totalDeclared,
                2,
                MidpointRounding.ToEven);

            var solicitantId = await _db.Solicitants
                .Where(s => s.NationalIdHash == row.InsuredPersonNationalIdHash && s.IsActive)
                .Select(s => (long?)s.Id)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (solicitantId is null)
            {
                unresolvedCount++;
                continue;
            }
            var accountId = await _db.PersonalAccounts
                .Where(p => p.OwnerSolicitantId == solicitantId.Value && p.IsActive)
                .Select(p => (long?)p.Id)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (accountId is null)
            {
                unresolvedCount++;
                continue;
            }

            await UpsertEntryAsync(
                accountId.Value,
                year,
                month,
                contributionBaseAmount: row.ContributionBaseAmount,
                contributionPaidAmount: share,
                now,
                ct).ConfigureAwait(false);
            actuallyDistributed += share;
            distributedToCount++;
        }

        var remainder = receipt.AmountReceived - actuallyDistributed;
        if (unresolvedCount == 0 && distributedToCount > 0 && remainder == 0m)
        {
            receipt.DistributionStatus = TreasuryPaymentDistributionStatus.Distributed;
            receipt.UndistributedRemainderAmount = null;
            receipt.DistributionFailureReason = null;
        }
        else if (distributedToCount == 0)
        {
            // Every row resolved unmatched (no Solicitant / no PA) — surface
            // the full amount as residual.
            receipt.DistributionStatus = TreasuryPaymentDistributionStatus.Failed;
            receipt.DistributionFailureReason = NoRev5ToDistributeMessage;
            receipt.UndistributedRemainderAmount = receipt.AmountReceived;
        }
        else
        {
            receipt.DistributionStatus = TreasuryPaymentDistributionStatus.PartiallyDistributed;
            receipt.UndistributedRemainderAmount = remainder;
            receipt.DistributionFailureReason = null;
        }
        receipt.DistributedAtUtc = now;
        receipt.UpdatedAtUtc = now;
        receipt.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RecordDistributionAuditAsync(receipt, distributedToCount, ct).ConfigureAwait(false);
        CnasMeter.TreasuryDistributed.Add(
            1,
            new KeyValuePair<string, object?>("outcome", OutcomeTag(receipt.DistributionStatus)));

        return Result<TreasuryPaymentReceiptDto>.Success(ToDto(receipt));
    }

    /// <inheritdoc />
    public async Task<Result<TreasuryPaymentReceiptDto>> GetAsync(
        long receiptId,
        CancellationToken ct = default)
    {
        var receipt = await _db.TreasuryPaymentReceipts
            .SingleOrDefaultAsync(r => r.Id == receiptId && r.IsActive, ct).ConfigureAwait(false);
        return receipt is null
            ? Result<TreasuryPaymentReceiptDto>.Failure(ErrorCodes.NotFound, "Treasury payment receipt not found.")
            : Result<TreasuryPaymentReceiptDto>.Success(ToDto(receipt));
    }

    /// <summary>
    /// Upserts a <see cref="PersonalAccountEntry"/> identified by
    /// <c>(PersonalAccountId, Year, Month, SourceCode = "TREASURY")</c>. When
    /// a previous Treasury entry already exists for the same bucket the
    /// distributed amount is ADDED to the existing paid value — multiple
    /// receipts in the same month accumulate.
    /// </summary>
    /// <param name="personalAccountId">Owning personal-account id.</param>
    /// <param name="year">Calendar year of the contribution.</param>
    /// <param name="month">Calendar month of the contribution (1..12).</param>
    /// <param name="contributionBaseAmount">Gross salary subject to contribution (MDL).</param>
    /// <param name="contributionPaidAmount">Distributed share for this row (MDL).</param>
    /// <param name="now">Current UTC instant from <see cref="ICnasTimeProvider"/>.</param>
    /// <param name="ct">Standard cancellation token.</param>
    private async Task UpsertEntryAsync(
        long personalAccountId,
        int year,
        int month,
        decimal contributionBaseAmount,
        decimal contributionPaidAmount,
        DateTime now,
        CancellationToken ct)
    {
        var existing = await _db.PersonalAccountEntries
            .SingleOrDefaultAsync(
                e => e.PersonalAccountId == personalAccountId &&
                     e.Year == year &&
                     e.Month == month &&
                     e.SourceCode == PersonalAccountSourceCode,
                ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Multiple Treasury receipts for the same (account, month) bucket
            // accumulate. The base amount is replaced rather than summed —
            // the REV-5 row's ContributionBaseAmount is the canonical figure.
            existing.ContributionBaseAmount = contributionBaseAmount;
            existing.ContributionPaidAmount += contributionPaidAmount;
            existing.IsActive = true;
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = _caller.UserSqid;
        }
        else
        {
            _db.PersonalAccountEntries.Add(new PersonalAccountEntry
            {
                PersonalAccountId = personalAccountId,
                Year = year,
                Month = month,
                ContributionBaseAmount = contributionBaseAmount,
                ContributionPaidAmount = contributionPaidAmount,
                SourceCode = PersonalAccountSourceCode,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            });
        }
    }

    /// <summary>
    /// Emits the Critical-severity <see cref="AuditDistributed"/> audit row
    /// with the canonical details payload.
    /// </summary>
    /// <param name="receipt">Updated receipt entity.</param>
    /// <param name="distributedToCount">Count of personal accounts credited.</param>
    /// <param name="ct">Standard cancellation token.</param>
    private async Task RecordDistributionAuditAsync(
        TreasuryPaymentReceipt receipt,
        int distributedToCount,
        CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            receiptSqid = _sqids.Encode(receipt.Id),
            receipt.AmountReceived,
            status = receipt.DistributionStatus.ToString(),
            distributedToCount,
            remainder = receipt.UndistributedRemainderAmount,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditDistributed,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(TreasuryPaymentReceipt),
            receipt.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps the terminal <see cref="TreasuryPaymentDistributionStatus"/> to
    /// a bounded-cardinality outcome tag for the
    /// <see cref="CnasMeter.TreasuryDistributed"/> counter.
    /// </summary>
    /// <param name="status">Terminal status set by the distribution path.</param>
    /// <returns>Stable lowercase tag.</returns>
    private static string OutcomeTag(TreasuryPaymentDistributionStatus status) => status switch
    {
        TreasuryPaymentDistributionStatus.Distributed => "distributed",
        TreasuryPaymentDistributionStatus.PartiallyDistributed => "partial",
        TreasuryPaymentDistributionStatus.Failed => "failed",
        _ => "pending",
    };

    /// <summary>Projects an entity into its outbound DTO with Sqid-encoded ids.</summary>
    /// <param name="r">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private TreasuryPaymentReceiptDto ToDto(TreasuryPaymentReceipt r) => new(
        Id: _sqids.Encode(r.Id),
        TreasuryReferenceNumber: r.TreasuryReferenceNumber,
        ReceiptDate: r.ReceiptDate,
        PayerContributorSqid: _sqids.Encode(r.PayerContributorId),
        ReportingMonth: r.ReportingMonth,
        AmountReceived: r.AmountReceived,
        DistributionStatus: r.DistributionStatus.ToString(),
        DistributedAtUtc: r.DistributedAtUtc,
        DistributionFailureReason: r.DistributionFailureReason,
        UndistributedRemainderAmount: r.UndistributedRemainderAmount);
}
