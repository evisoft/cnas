using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.PersonalAccount;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0516 / TOR CF 02.04 — implementation of
/// <see cref="IPersonalAccountExtractService"/>. Aggregates contribution
/// entries from <see cref="PersonalAccountEntry"/> into the per-year /
/// grand-total shape exposed by <see cref="PersonalAccountExtractDto"/>.
/// </summary>
public sealed class PersonalAccountExtractService : IPersonalAccountExtractService
{
    /// <summary>Audit event code emitted on every successful extract generation.</summary>
    public const string AuditEventCode = "PERSONAL_ACCOUNT.EXTRACT_GENERATED";

    /// <summary>Permission required by <see cref="GetForSolicitantAsync"/>.</summary>
    public const string ReadAnyPermission = "PersonalAccount.ReadAny";

    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context for PersonalAccount + PersonalAccountEntry lookups.</param>
    /// <param name="sqids">Sqid encoder used to render the Solicitant id on the output DTO.</param>
    /// <param name="caller">Per-request caller context — used to resolve the current user's Solicitant + permission check.</param>
    /// <param name="clock">UTC clock used to stamp <see cref="PersonalAccountExtractDto.GeneratedAtUtc"/>.</param>
    /// <param name="audit">Audit-log façade.</param>
    public PersonalAccountExtractService(
        ICnasDbContext db,
        ISqidService sqids,
        ICallerContext caller,
        ICnasTimeProvider clock,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);
        _db = db;
        _sqids = sqids;
        _caller = caller;
        _clock = clock;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<PersonalAccountExtractDto>> GetForCurrentUserAsync(CancellationToken ct = default)
    {
        // 1. Defense in depth — the controller carries [Authorize], but
        //    internal callers could bypass it.
        if (_caller.UserId is not long userId)
        {
            return Result<PersonalAccountExtractDto>.Failure(
                ErrorCodes.Unauthorized,
                "Extract requires an authenticated caller.");
        }

        // 2. Resolve the caller's Solicitant via the canonical
        //    UserProfile→Solicitant identity link (matched on the deterministic
        //    NationalIdHash shadow column). Mirrors the pattern in
        //    WorkflowNotificationOrchestrator.ResolveByRoleAsync for the
        //    "Applicant" recipient role.
        var nationalIdHash = await _db.UserProfiles
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.NationalIdHash)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(nationalIdHash))
        {
            return Result<PersonalAccountExtractDto>.Failure(
                ErrorCodes.NotFound,
                "No Solicitant is linked to the calling user.");
        }

        var solicitantId = await _db.Solicitants
            .Where(s => s.NationalIdHash == nationalIdHash && s.IsActive)
            .Select(s => (long?)s.Id)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (solicitantId is null)
        {
            return Result<PersonalAccountExtractDto>.Failure(
                ErrorCodes.NotFound,
                "No Solicitant is linked to the calling user.");
        }

        return await BuildExtractAsync(solicitantId.Value, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<PersonalAccountExtractDto>> GetForSolicitantAsync(
        long solicitantId,
        CancellationToken ct = default)
    {
        // Permission gate — only callers carrying the explicit ReadAny
        // permission may pull arbitrary citizen extracts. Anonymous and
        // standard-user callers receive Forbidden.
        if (!_caller.Roles.Contains(ReadAnyPermission, StringComparer.Ordinal))
        {
            return Result<PersonalAccountExtractDto>.Failure(
                ErrorCodes.Forbidden,
                $"Permission '{ReadAnyPermission}' is required to read arbitrary personal-account extracts.");
        }

        return await BuildExtractAsync(solicitantId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the personal account for <paramref name="solicitantId"/>, pulls
    /// its entries, aggregates them per year, writes the audit row, and
    /// returns the populated DTO.
    /// </summary>
    /// <param name="solicitantId">Raw bigint id of the target Solicitant.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Populated extract DTO or a <see cref="ErrorCodes.NotFound"/> failure.</returns>
    private async Task<Result<PersonalAccountExtractDto>> BuildExtractAsync(long solicitantId, CancellationToken ct)
    {
        var account = await _db.PersonalAccounts
            .Where(p => p.OwnerSolicitantId == solicitantId && p.IsActive)
            .Select(p => new { p.Id, p.AccountCode })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (account is null)
        {
            return Result<PersonalAccountExtractDto>.Failure(
                ErrorCodes.NotFound,
                "No personal account is on file for the supplied Solicitant.");
        }

        // Materialise all active entries — the in-memory aggregation below
        // keeps the LINQ tree friendly to both the InMemory test provider and
        // the production Postgres provider. Volumes are bounded (decades of
        // monthly entries), so the read fits inside a single round-trip.
        var entries = await _db.PersonalAccountEntries
            .Where(e => e.PersonalAccountId == account.Id && e.IsActive)
            .Select(e => new
            {
                e.Year,
                e.Month,
                e.ContributionBaseAmount,
                e.ContributionPaidAmount,
                e.SourceCode,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Group by year (DESC), order entries within a year by Month ASC. The
        // per-month "Months" counter projects the count of distinct months —
        // multiple sources per month collapse to one for the count but stay
        // expanded in the entry list.
        var yearBuckets = entries
            .GroupBy(e => e.Year)
            .OrderByDescending(g => g.Key)
            .Select(g => new PersonalAccountYearDto(
                Year: g.Key,
                Months: g.Select(e => e.Month).Distinct().Count(),
                TotalContributionBase: g.Sum(e => e.ContributionBaseAmount),
                TotalContributionPaid: g.Sum(e => e.ContributionPaidAmount),
                Entries: g
                    .OrderBy(e => e.Month)
                    .Select(e => new PersonalAccountEntryDto(
                        Month: e.Month,
                        ContributionBaseAmount: e.ContributionBaseAmount,
                        ContributionPaidAmount: e.ContributionPaidAmount,
                        SourceCode: e.SourceCode))
                    .ToList()))
            .ToList();

        var grandTotalContributions = entries.Sum(e => e.ContributionPaidAmount);
        var grandTotalMonths = entries
            .Select(e => (e.Year, e.Month))
            .Distinct()
            .Count();

        var solicitantSqid = _sqids.Encode(solicitantId);

        var dto = new PersonalAccountExtractDto(
            AccountCodeSqid: account.AccountCode,
            SolicitantSqid: solicitantSqid,
            Years: yearBuckets,
            GrandTotalContributions: grandTotalContributions,
            GrandTotalMonths: grandTotalMonths,
            GeneratedAtUtc: _clock.UtcNow);

        // Audit Sensitive — access to citizen financial-history data.
        var details = JsonSerializer.Serialize(new
        {
            solicitantSqid,
            monthsTotal = grandTotalMonths,
            yearsCount = yearBuckets.Count,
        });
        await _audit.RecordAsync(
            eventCode: AuditEventCode,
            severity: AuditSeverity.Sensitive,
            actorId: _caller.UserSqid ?? "anonymous",
            targetEntity: nameof(PersonalAccount),
            targetEntityId: account.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        return Result<PersonalAccountExtractDto>.Success(dto);
    }
}
