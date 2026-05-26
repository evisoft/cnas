using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ManagementPeriods;
using Cnas.Ps.Application.Rev5;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Rev5;

/// <summary>
/// R0910 / TOR BP 2.2-A — concrete implementation of
/// <see cref="IRev5DeclarationService"/>. Owns the REV-5 registration /
/// adjust / cancel paths and the projection into
/// <c>PersonalAccountEntry</c>.
/// </summary>
public sealed class Rev5DeclarationService : IRev5DeclarationService
{
    /// <summary>Stable audit event code emitted on a successful registration.</summary>
    public const string AuditRegistered = "REV5.REGISTERED";

    /// <summary>Stable audit event code emitted on a per-row adjustment.</summary>
    public const string AuditAdjusted = "REV5.ROW_ADJUSTED";

    /// <summary>Stable audit event code emitted on a cancellation.</summary>
    public const string AuditCancelled = "REV5.CANCELLED";

    /// <summary>Stable failure message used when the natural-key index rejects the insert.</summary>
    public const string DuplicateMessage = "REV5_DUPLICATE";

    /// <summary>Stable failure message used when the reporting month is closed (R0820).</summary>
    public const string MonthClosedMessage = "MONTH_CLOSED";

    /// <summary>
    /// Stable source-code stamped on every <see cref="PersonalAccountEntry"/>
    /// projected from a REV-5 row. Distinct from R0913 adjustments, which
    /// carry the document-source code.
    /// </summary>
    public const string PersonalAccountSourceCode = "REV5";

    /// <summary>
    /// Length of the IDNP-hash prefix surfaced on the response for unmatched
    /// rows. Trade-off between operator usefulness and anti-enumeration.
    /// </summary>
    public const int HashPrefixLength = 8;

    /// <summary>
    /// Maximum number of unmatched-hash prefixes returned on the response
    /// envelope. Bounded so the response stays small even when an entire
    /// declaration misses (e.g. brand-new employer).
    /// </summary>
    public const int MaxUnmatchedPrefixes = 10;

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
    private readonly IManagementPeriodService _periods;
    private readonly IValidator<Rev5DeclarationRegisterInputDto> _registerValidator;
    private readonly IValidator<Rev5DeclarationRowAdjustInputDto> _adjustValidator;
    private readonly IValidator<Rev5DeclarationCancelInputDto> _cancelValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="periods">R0820 management-period service consulted before every registration.</param>
    /// <param name="registerValidator">Validator for the registration-input shape.</param>
    /// <param name="adjustValidator">Validator for the row-adjustment-input shape.</param>
    /// <param name="cancelValidator">Validator for the cancellation-input shape.</param>
    public Rev5DeclarationService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IManagementPeriodService periods,
        IValidator<Rev5DeclarationRegisterInputDto> registerValidator,
        IValidator<Rev5DeclarationRowAdjustInputDto> adjustValidator,
        IValidator<Rev5DeclarationCancelInputDto> cancelValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentNullException.ThrowIfNull(registerValidator);
        ArgumentNullException.ThrowIfNull(adjustValidator);
        ArgumentNullException.ThrowIfNull(cancelValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _periods = periods;
        _registerValidator = registerValidator;
        _adjustValidator = adjustValidator;
        _cancelValidator = cancelValidator;
    }

    /// <inheritdoc />
    public async Task<Result<Rev5DeclarationDto>> RegisterAsync(
        Rev5DeclarationRegisterInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _registerValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            CnasMeter.Rev5Registered.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            return Result<Rev5DeclarationDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.FilingContributorSqid);
        if (decoded.IsFailure)
        {
            CnasMeter.Rev5Registered.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            return Result<Rev5DeclarationDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var filingContributorId = decoded.Value;

        // Defensive employer-existence check — the row has no navigation
        // property, so a bogus FilingContributorId would persist dangling
        // children otherwise.
        var employerExists = await _db.Contributors
            .AnyAsync(c => c.Id == filingContributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (!employerExists)
        {
            CnasMeter.Rev5Registered.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            return Result<Rev5DeclarationDto>.Failure(ErrorCodes.NotFound, "Filing employer not found.");
        }

        // R0820 — refuse registration into a closed management period.
        if (await _periods.IsMonthClosedAsync(input.ReportingMonth, ct).ConfigureAwait(false))
        {
            CnasMeter.Rev5Registered.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            return Result<Rev5DeclarationDto>.Failure(ErrorCodes.ValidationFailed, MonthClosedMessage);
        }

        // Natural-key duplicate probe — surface a stable Conflict rather than
        // relying on the database to throw (InMemory test provider has no
        // unique-index enforcement).
        var duplicate = await _db.Rev5Declarations.AnyAsync(d =>
            d.FilingContributorId == filingContributorId &&
            d.ReportingMonth == input.ReportingMonth &&
            d.ReferenceNumber == input.ReferenceNumber &&
            d.IsActive,
            ct).ConfigureAwait(false);
        if (duplicate)
        {
            CnasMeter.Rev5Registered.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
            return Result<Rev5DeclarationDto>.Failure(ErrorCodes.Conflict, DuplicateMessage);
        }

        var now = _clock.UtcNow;
        var totalDeclared = 0m;
        foreach (var row in input.Rows)
        {
            totalDeclared += row.ContributionAmount;
        }

        var header = new Rev5Declaration
        {
            FilingContributorId = filingContributorId,
            ReportingMonth = input.ReportingMonth,
            FiledAtUtc = input.FiledAtUtc ?? now,
            ReferenceNumber = input.ReferenceNumber,
            Status = Rev5DeclarationStatus.Received,
            TotalDeclaredAmount = totalDeclared,
            RowCount = input.Rows.Count,
            Notes = input.Notes,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.Rev5Declarations.Add(header);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Persist every child row (resolved or not) — we never reject the
        // whole declaration just because some hashes do not match a known
        // Solicitant. Track unmatched hashes for the response envelope.
        var unmatchedHashes = new List<string>();
        var rowEntities = new List<Rev5DeclarationRow>(input.Rows.Count);
        foreach (var row in input.Rows)
        {
            var entity = new Rev5DeclarationRow
            {
                Rev5DeclarationId = header.Id,
                InsuredPersonNationalIdHash = row.InsuredPersonNationalIdHash,
                ContributionBaseAmount = row.ContributionBaseAmount,
                ContributionAmount = row.ContributionAmount,
                DaysWorked = row.DaysWorked,
                PositionCode = row.PositionCode,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.Rev5DeclarationRows.Add(entity);
            rowEntities.Add(entity);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Project resolved rows into PersonalAccountEntry. The lookup loop
        // resolves each IDNP hash to a Solicitant -> PersonalAccount, then
        // upserts the (PersonalAccountId, Year, Month, "REV5") entry.
        var year = input.ReportingMonth.Year;
        var month = input.ReportingMonth.Month;
        foreach (var row in rowEntities)
        {
            var solicitantId = await _db.Solicitants
                .Where(s => s.NationalIdHash == row.InsuredPersonNationalIdHash && s.IsActive)
                .Select(s => (long?)s.Id)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (solicitantId is null)
            {
                unmatchedHashes.Add(row.InsuredPersonNationalIdHash);
                continue;
            }

            var accountId = await _db.PersonalAccounts
                .Where(p => p.OwnerSolicitantId == solicitantId.Value && p.IsActive)
                .Select(p => (long?)p.Id)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (accountId is null)
            {
                // Solicitant exists but has no personal account on file —
                // treat as unmatched so operators surface the missing PA.
                unmatchedHashes.Add(row.InsuredPersonNationalIdHash);
                continue;
            }

            await UpsertEntryAsync(
                accountId.Value,
                year,
                month,
                row.ContributionBaseAmount,
                row.ContributionAmount,
                PersonalAccountSourceCode,
                now,
                ct).ConfigureAwait(false);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (unmatchedHashes.Count > 0)
        {
            CnasMeter.Rev5RowsUnmatched.Add(unmatchedHashes.Count);
        }

        var unmatchedPrefixes = BuildPrefixList(unmatchedHashes);
        var details = JsonSerializer.Serialize(
            new
            {
                rev5DeclarationSqid = _sqids.Encode(header.Id),
                employerSqid = _sqids.Encode(filingContributorId),
                month = input.ReportingMonth.ToString("O", CultureInfo.InvariantCulture),
                rowCount = input.Rows.Count,
                unmatchedCount = unmatchedHashes.Count,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditRegistered,
            AuditSeverity.Information,
            _caller.UserSqid ?? "?",
            nameof(Rev5Declaration),
            header.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.Rev5Registered.Add(1, new KeyValuePair<string, object?>("outcome", "success"));

        return Result<Rev5DeclarationDto>.Success(ToDto(header, unmatchedHashes.Count, unmatchedPrefixes));
    }

    /// <inheritdoc />
    public async Task<Result<Rev5DeclarationDto>> AdjustRowAsync(
        long rev5DeclarationId,
        string insuredPersonNationalIdHash,
        decimal adjustedContributionAmount,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(insuredPersonNationalIdHash);
        ArgumentNullException.ThrowIfNull(reason);

        var input = new Rev5DeclarationRowAdjustInputDto(adjustedContributionAmount, reason);
        var validation = await _adjustValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<Rev5DeclarationDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var header = await _db.Rev5Declarations
            .SingleOrDefaultAsync(d => d.Id == rev5DeclarationId && d.IsActive, ct)
            .ConfigureAwait(false);
        if (header is null)
        {
            return Result<Rev5DeclarationDto>.Failure(ErrorCodes.NotFound, "REV-5 declaration not found.");
        }
        if (header.Status == Rev5DeclarationStatus.Cancelled)
        {
            return Result<Rev5DeclarationDto>.Failure(
                ErrorCodes.Conflict,
                "Cannot adjust a cancelled REV-5 declaration.");
        }

        var row = await _db.Rev5DeclarationRows
            .SingleOrDefaultAsync(
                r => r.Rev5DeclarationId == header.Id &&
                     r.InsuredPersonNationalIdHash == insuredPersonNationalIdHash &&
                     r.IsActive,
                ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<Rev5DeclarationDto>.Failure(ErrorCodes.NotFound, "REV-5 row not found.");
        }

        var now = _clock.UtcNow;
        var delta = adjustedContributionAmount - row.ContributionAmount;
        row.ContributionAmount = adjustedContributionAmount;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;

        header.TotalDeclaredAmount += delta;
        header.Status = Rev5DeclarationStatus.Adjusted;
        header.UpdatedAtUtc = now;
        header.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Re-project the PersonalAccountEntry — same upsert path as during
        // initial registration so the citizen-facing extract stays current.
        var solicitantId = await _db.Solicitants
            .Where(s => s.NationalIdHash == row.InsuredPersonNationalIdHash && s.IsActive)
            .Select(s => (long?)s.Id)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (solicitantId is not null)
        {
            var accountId = await _db.PersonalAccounts
                .Where(p => p.OwnerSolicitantId == solicitantId.Value && p.IsActive)
                .Select(p => (long?)p.Id)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (accountId is not null)
            {
                await UpsertEntryAsync(
                    accountId.Value,
                    header.ReportingMonth.Year,
                    header.ReportingMonth.Month,
                    row.ContributionBaseAmount,
                    row.ContributionAmount,
                    PersonalAccountSourceCode,
                    now,
                    ct).ConfigureAwait(false);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        var details = JsonSerializer.Serialize(
            new
            {
                rev5DeclarationSqid = _sqids.Encode(header.Id),
                rowSqid = _sqids.Encode(row.Id),
                adjustedContributionAmount,
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditAdjusted,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Rev5Declaration),
            header.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<Rev5DeclarationDto>.Success(ToDto(header, unmatchedCount: 0, unmatchedPrefixes: []));
    }

    /// <inheritdoc />
    public async Task<Result> CancelAsync(
        long rev5DeclarationId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var input = new Rev5DeclarationCancelInputDto(reason);
        var validation = await _cancelValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var header = await _db.Rev5Declarations
            .SingleOrDefaultAsync(d => d.Id == rev5DeclarationId && d.IsActive, ct)
            .ConfigureAwait(false);
        if (header is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "REV-5 declaration not found.");
        }
        if (header.Status == Rev5DeclarationStatus.Cancelled)
        {
            return Result.Failure(ErrorCodes.Conflict, "REV-5 declaration is already cancelled.");
        }

        var now = _clock.UtcNow;
        header.Status = Rev5DeclarationStatus.Cancelled;
        header.Notes = reason;
        header.UpdatedAtUtc = now;
        header.UpdatedBy = _caller.UserSqid;

        // Roll back every projected PersonalAccountEntry. We identify the
        // entries by (PersonalAccountId, Year, Month, "REV5") for each row
        // whose hash resolves to a known Solicitant.
        var rows = await _db.Rev5DeclarationRows
            .Where(r => r.Rev5DeclarationId == header.Id && r.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var year = header.ReportingMonth.Year;
        var month = header.ReportingMonth.Month;
        foreach (var row in rows)
        {
            var solicitantId = await _db.Solicitants
                .Where(s => s.NationalIdHash == row.InsuredPersonNationalIdHash && s.IsActive)
                .Select(s => (long?)s.Id)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (solicitantId is null)
            {
                continue;
            }
            var accountId = await _db.PersonalAccounts
                .Where(p => p.OwnerSolicitantId == solicitantId.Value && p.IsActive)
                .Select(p => (long?)p.Id)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (accountId is null)
            {
                continue;
            }

            var entry = await _db.PersonalAccountEntries
                .SingleOrDefaultAsync(
                    e => e.PersonalAccountId == accountId.Value &&
                         e.Year == year &&
                         e.Month == month &&
                         e.SourceCode == PersonalAccountSourceCode &&
                         e.IsActive,
                    ct)
                .ConfigureAwait(false);
            if (entry is not null)
            {
                entry.IsActive = false;
                entry.UpdatedAtUtc = now;
                entry.UpdatedBy = _caller.UserSqid;
            }
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                rev5DeclarationSqid = _sqids.Encode(header.Id),
                month = header.ReportingMonth.ToString("O", CultureInfo.InvariantCulture),
                rowCount = rows.Count,
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCancelled,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Rev5Declaration),
            header.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Rev5DeclarationDto?> GetAsync(long id, CancellationToken ct = default)
    {
        var header = await _db.Rev5Declarations
            .SingleOrDefaultAsync(d => d.Id == id && d.IsActive, ct)
            .ConfigureAwait(false);
        return header is null ? null : ToDto(header, unmatchedCount: 0, unmatchedPrefixes: []);
    }

    /// <summary>
    /// Upserts a <see cref="PersonalAccountEntry"/> identified by
    /// <c>(PersonalAccountId, Year, Month, SourceCode)</c>. Reactivates a
    /// soft-deleted match rather than re-inserting a duplicate so the
    /// natural-key index stays satisfied.
    /// </summary>
    /// <param name="personalAccountId">Owning personal-account id.</param>
    /// <param name="year">Calendar year of the contribution.</param>
    /// <param name="month">Calendar month of the contribution (1..12).</param>
    /// <param name="contributionBaseAmount">Gross salary subject to contribution (MDL).</param>
    /// <param name="contributionPaidAmount">Contribution paid (MDL).</param>
    /// <param name="sourceCode">Stable source-code stamped on the entry.</param>
    /// <param name="now">Current UTC instant from <see cref="ICnasTimeProvider"/>.</param>
    /// <param name="ct">Standard cancellation token.</param>
    private async Task UpsertEntryAsync(
        long personalAccountId,
        int year,
        int month,
        decimal contributionBaseAmount,
        decimal contributionPaidAmount,
        string sourceCode,
        DateTime now,
        CancellationToken ct)
    {
        var existing = await _db.PersonalAccountEntries
            .SingleOrDefaultAsync(
                e => e.PersonalAccountId == personalAccountId &&
                     e.Year == year &&
                     e.Month == month &&
                     e.SourceCode == sourceCode,
                ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            existing.ContributionBaseAmount = contributionBaseAmount;
            existing.ContributionPaidAmount = contributionPaidAmount;
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
                SourceCode = sourceCode,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            });
        }
    }

    /// <summary>
    /// Builds the bounded list of IDNP-hash prefixes returned on the response
    /// envelope. Caps at <see cref="MaxUnmatchedPrefixes"/> entries and uses
    /// <see cref="HashPrefixLength"/> characters per prefix.
    /// </summary>
    /// <param name="unmatchedHashes">Full unmatched-hash list collected during registration.</param>
    /// <returns>Bounded prefix list (may be shorter than the cap when fewer hashes were unmatched).</returns>
    private static IReadOnlyList<string> BuildPrefixList(IReadOnlyList<string> unmatchedHashes)
    {
        if (unmatchedHashes.Count == 0)
        {
            return [];
        }
        var capacity = Math.Min(unmatchedHashes.Count, MaxUnmatchedPrefixes);
        var prefixes = new List<string>(capacity);
        for (var i = 0; i < capacity; i++)
        {
            var hash = unmatchedHashes[i];
            prefixes.Add(hash.Length <= HashPrefixLength ? hash : hash[..HashPrefixLength]);
        }
        return prefixes;
    }

    /// <summary>Projects a <see cref="Rev5Declaration"/> entity into its outbound DTO.</summary>
    /// <param name="entity">Loaded entity.</param>
    /// <param name="unmatchedCount">Number of unmatched rows at registration time.</param>
    /// <param name="unmatchedPrefixes">Bounded prefix list — may be empty.</param>
    /// <returns>Populated DTO.</returns>
    private Rev5DeclarationDto ToDto(
        Rev5Declaration entity,
        int unmatchedCount,
        IReadOnlyList<string> unmatchedPrefixes) => new(
            Id: _sqids.Encode(entity.Id),
            FilingContributorSqid: _sqids.Encode(entity.FilingContributorId),
            ReportingMonth: entity.ReportingMonth,
            FiledAtUtc: entity.FiledAtUtc,
            ReferenceNumber: entity.ReferenceNumber,
            Status: entity.Status.ToString(),
            TotalDeclaredAmount: entity.TotalDeclaredAmount,
            RowCount: entity.RowCount,
            UnmatchedRowCount: unmatchedCount,
            UnmatchedNationalIdHashPrefixes: unmatchedPrefixes,
            Notes: entity.Notes);
}
