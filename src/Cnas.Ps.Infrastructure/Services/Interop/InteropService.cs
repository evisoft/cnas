using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Interop;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts.Interop;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Interop;

/// <summary>
/// R0634 / TOR CF 14.12 / Annex 4 — implementation of
/// <see cref="IInteropApi"/>. Resolves the supplied IDNP to its
/// <c>Solicitant</c> via the deterministic <c>NationalIdHash</c> shadow
/// column, then projects the relevant aggregate (PersonalAccount,
/// PersonalAccountEntry, BenefitPayment) into the Annex-4 response shape.
/// Every call writes one Sensitive audit row carrying only the IDNP hash
/// prefix; the raw IDNP never reaches the audit pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only data path.</b> The service injects
/// <see cref="IReadOnlyCnasDbContext"/> so all Annex-4 queries route to the
/// streaming replica per TOR PSR 006 / ARH 025. Interop callers tolerate
/// the millisecond-scale replica lag because they are not the source of
/// truth for any CNAS write.
/// </para>
/// <para>
/// <b>Hash-prefix format.</b> The audit payload + the response DTO both
/// carry the first 8 lower-case hex characters of the HMAC-SHA256 hash —
/// i.e. the first 4 raw bytes of the deterministic hash rendered as hex.
/// The hex form is required by the Annex-4 contract (the consuming systems
/// expect <c>[0-9a-f]{8}</c>) and is forward-compatible with whatever
/// alphabet the deterministic-hasher emits internally.
/// </para>
/// </remarks>
public sealed class InteropService : IInteropApi
{
    /// <summary>Audit event code for <c>GetInsuredPersonStatus</c>.</summary>
    public const string AuditInsuredPersonStatus = "INTEROP.INSURED_PERSON_STATUS.QUERIED";

    /// <summary>Audit event code for <c>GetContributionHistory</c>.</summary>
    public const string AuditContributionHistory = "INTEROP.CONTRIBUTION_HISTORY.QUERIED";

    /// <summary>Audit event code for <c>GetBenefitsList</c>.</summary>
    public const string AuditBenefitsList = "INTEROP.BENEFITS_LIST.QUERIED";

    /// <summary>Audit event code for <c>GetPersonalAccountSnapshot</c>.</summary>
    public const string AuditPersonalAccountSnapshot = "INTEROP.PERSONAL_ACCOUNT_SNAPSHOT.QUERIED";

    /// <summary>Audit event code for <c>GetActiveDecisions</c> (R1702).</summary>
    public const string AuditActiveDecisions = "INTEROP.ACTIVE_DECISIONS.QUERIED";

    /// <summary>Audit event code for <c>GetPaymentStatus</c> (R1703).</summary>
    public const string AuditPaymentStatus = "INTEROP.PAYMENT_STATUS.QUERIED";

    /// <summary>Audit event code for <c>GetPayerData</c> (R1704).</summary>
    public const string AuditPayerData = "INTEROP.PAYER_DATA.QUERIED";

    /// <summary>Audit event code for <c>IsBenefitBeneficiary</c> (R1705).</summary>
    public const string AuditIsBenefitBeneficiary = "INTEROP.IS_BENEFIT_BENEFICIARY.QUERIED";

    /// <summary>Audit event code for <c>GetContributionPaymentInfo</c> (R1706).</summary>
    public const string AuditContributionPaymentInfo = "INTEROP.CONTRIBUTION_PAYMENT_INFO.QUERIED";

    /// <summary>Audit event code for <c>GetLegalApplicableForm</c> (R1707).</summary>
    public const string AuditLegalApplicableForm = "INTEROP.LEGAL_APPLICABLE_FORM.QUERIED";

    /// <summary>Audit event code for <c>GetWorkInsurancePeriod</c> (R1708).</summary>
    public const string AuditWorkInsurancePeriod = "INTEROP.WORK_INSURANCE_PERIOD.QUERIED";

    /// <summary>Op-name tag value for the <c>op_name</c> counter dimension on <c>GetInsuredPersonStatus</c>.</summary>
    public const string OpNameInsuredPersonStatus = "GetInsuredPersonStatus";

    /// <summary>Op-name tag value for <c>GetContributionHistory</c>.</summary>
    public const string OpNameContributionHistory = "GetContributionHistory";

    /// <summary>Op-name tag value for <c>GetBenefitsList</c>.</summary>
    public const string OpNameBenefitsList = "GetBenefitsList";

    /// <summary>Op-name tag value for <c>GetPersonalAccountSnapshot</c>.</summary>
    public const string OpNamePersonalAccountSnapshot = "GetPersonalAccountSnapshot";

    /// <summary>Op-name tag value for <c>GetActiveDecisions</c> (R1702).</summary>
    public const string OpNameActiveDecisions = "GetActiveDecisions";

    /// <summary>Op-name tag value for <c>GetPaymentStatus</c> (R1703).</summary>
    public const string OpNamePaymentStatus = "GetPaymentStatus";

    /// <summary>Op-name tag value for <c>GetPayerData</c> (R1704).</summary>
    public const string OpNamePayerData = "GetPayerData";

    /// <summary>Op-name tag value for <c>IsBenefitBeneficiary</c> (R1705).</summary>
    public const string OpNameIsBenefitBeneficiary = "IsBenefitBeneficiary";

    /// <summary>Op-name tag value for <c>GetContributionPaymentInfo</c> (R1706).</summary>
    public const string OpNameContributionPaymentInfo = "GetContributionPaymentInfo";

    /// <summary>Op-name tag value for <c>GetLegalApplicableForm</c> (R1707).</summary>
    public const string OpNameLegalApplicableForm = "GetLegalApplicableForm";

    /// <summary>Op-name tag value for <c>GetWorkInsurancePeriod</c> (R1708).</summary>
    public const string OpNameWorkInsurancePeriod = "GetWorkInsurancePeriod";

    /// <summary>
    /// Number of hex characters from the deterministic IDNP hash that
    /// surface in audit rows and the response DTO. Eight characters give 4
    /// bytes of entropy — sufficient for forensic correlation across audit
    /// rows in the same investigation, insufficient for an attacker to
    /// brute-force back to the original IDNP. Mirrors the R0513
    /// <c>ExtractCnasCodeService.IdnpHashPrefixLength</c> discipline.
    /// </summary>
    public const int IdnpHashPrefixLength = 8;

    private readonly IReadOnlyCnasDbContext _db;
    private readonly IDeterministicHasher _hasher;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly IAuditService _audit;
    private readonly ICallerContext _caller;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">Read-only EF context routed to the streaming replica.</param>
    /// <param name="hasher">Deterministic IDNP hasher shared with the rest of the system.</param>
    /// <param name="clock">UTC clock used to stamp <c>AsOfUtc</c> fields on the response DTOs.</param>
    /// <param name="sqids">Sqid encoder — unused for Sqid-bearing output today but retained for forward-compatibility with future Annex-4 ops.</param>
    /// <param name="audit">Audit-log façade — every call writes one Sensitive row.</param>
    /// <param name="caller">Per-request caller context — used for sourceIp / correlation on audit rows.</param>
    public InteropService(
        IReadOnlyCnasDbContext db,
        IDeterministicHasher hasher,
        ICnasTimeProvider clock,
        ISqidService sqids,
        IAuditService audit,
        ICallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(caller);
        _db = db;
        _hasher = hasher;
        _clock = clock;
        _sqids = sqids;
        _audit = audit;
        _caller = caller;
    }

    /// <inheritdoc />
    public async Task<Result<InsuredPersonStatusDto>> GetInsuredPersonStatusAsync(
        string idnp,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNameInsuredPersonStatus);

        // 1. IDNP validation. Surface a stable error code instead of throwing —
        //    a malformed IDNP is a client bug, not an exceptional condition.
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<InsuredPersonStatusDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        var hashFull = _hasher.ComputeHash(idnpResult.Value.Value);
        var hashPrefix = HashPrefix(hashFull);

        // 2. Resolve the Solicitant (active) via the deterministic
        //    NationalIdHash shadow column. The query stays read-only via
        //    IReadOnlyCnasDbContext per the layer rationale on the type.
        var solicitant = await _db.Solicitants
            .Where(s => s.NationalIdHash == hashFull && s.IsActive)
            .Select(s => new { s.Id })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        InsuredPersonStatusDto dto;
        if (solicitant is null)
        {
            // 3a. Unknown / soft-deleted citizen — soft-404 shape so the
            //     caller does not have to wrap a probe in try/catch.
            dto = new InsuredPersonStatusDto(
                IdnpHashPrefix: hashPrefix,
                IsRegistered: false,
                AccountCode: null,
                ActiveBenefitsCount: 0,
                AsOfUtc: _clock.UtcNow);
        }
        else
        {
            // 3b. Known citizen — pull the personal-account code + active
            //     benefits count alongside.
            var accountCode = await _db.PersonalAccounts
                .Where(p => p.OwnerSolicitantId == solicitant.Id && p.IsActive)
                .Select(p => p.AccountCode)
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);

            var activeBenefitsCount = await _db.BenefitPayments
                .Where(b => b.BeneficiarySolicitantId == solicitant.Id && b.IsActive)
                .Select(b => b.BenefitType)
                .Distinct()
                .CountAsync(ct)
                .ConfigureAwait(false);

            dto = new InsuredPersonStatusDto(
                IdnpHashPrefix: hashPrefix,
                IsRegistered: true,
                AccountCode: accountCode,
                ActiveBenefitsCount: activeBenefitsCount,
                AsOfUtc: _clock.UtcNow);
        }

        // 4. Audit row — Sensitive severity, hash prefix only.
        await WriteAuditAsync(
            AuditInsuredPersonStatus,
            JsonSerializer.Serialize(new
            {
                idnpHashPrefix = hashPrefix,
                isRegistered = dto.IsRegistered,
                asOfUtc = dto.AsOfUtc,
            }),
            targetEntity: solicitant is null ? null : nameof(Solicitant),
            targetEntityId: solicitant?.Id,
            ct).ConfigureAwait(false);

        return Result<InsuredPersonStatusDto>.Success(dto);
    }

    /// <inheritdoc />
    public async Task<Result<ContributionHistoryDto>> GetContributionHistoryAsync(
        string idnp,
        DateOnly fromMonth,
        DateOnly toMonth,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNameContributionHistory);

        // 1. IDNP validation.
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<ContributionHistoryDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        // 2. Window validation — same contract as the validator so internal
        //    callers (no DTO envelope) are also gated.
        if (fromMonth > toMonth)
        {
            return Result<ContributionHistoryDto>.Failure(
                ErrorCodes.InvalidDateRange,
                "FromMonth must be on or before ToMonth.");
        }
        if (InteropContributionHistoryRequestValidator.ComputeMonthsInclusive(fromMonth, toMonth)
            > InteropContributionHistoryRequestValidator.MaxWindowMonths)
        {
            return Result<ContributionHistoryDto>.Failure(
                ErrorCodes.InvalidDateRange,
                $"Window must not exceed {InteropContributionHistoryRequestValidator.MaxWindowMonths} months.");
        }

        var hashFull = _hasher.ComputeHash(idnpResult.Value.Value);
        var hashPrefix = HashPrefix(hashFull);

        // 3. Resolve the Solicitant. Unknown IDNP → 404 (this op has no
        //    soft-404 shape; the response carries actual contribution
        //    data and an empty payload would be ambiguous).
        var solicitant = await _db.Solicitants
            .Where(s => s.NationalIdHash == hashFull && s.IsActive)
            .Select(s => new { s.Id })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (solicitant is null)
        {
            await WriteAuditAsync(
                AuditContributionHistory,
                JsonSerializer.Serialize(new
                {
                    idnpHashPrefix = hashPrefix,
                    isRegistered = false,
                    asOfUtc = _clock.UtcNow,
                }),
                targetEntity: null,
                targetEntityId: null,
                ct).ConfigureAwait(false);
            return Result<ContributionHistoryDto>.Failure(
                ErrorCodes.NotFound,
                "No active Solicitant on file for the supplied IDNP.");
        }

        // 4. Resolve the personal account. Citizens without one map to an
        //    empty-list response so the inter-system caller can distinguish
        //    "registered but no contributions" from "no account on file".
        var accountId = await _db.PersonalAccounts
            .Where(p => p.OwnerSolicitantId == solicitant.Id && p.IsActive)
            .Select(p => (long?)p.Id)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // 5. Materialise contribution rows inside the window. Volumes are
        //    bounded by the 60-month cap so a single round-trip is safe.
        //    Filter by (Year, Month) tuple rather than constructing a
        //    DateOnly column server-side — the latter blocks the indexed
        //    lookup on the (Year, Month) composite.
        var fromYear = fromMonth.Year;
        var fromMonthIdx = fromMonth.Month;
        var toYear = toMonth.Year;
        var toMonthIdx = toMonth.Month;

        List<ContributionMonthEntryDto> months;
        if (accountId is null)
        {
            months = new List<ContributionMonthEntryDto>();
        }
        else
        {
            // Bucket comparison: (Year, Month) lies in [fromYear/fromMonth,
            // toYear/toMonth] iff fromYear*12 + fromMonth ≤ Year*12 + Month
            // ≤ toYear*12 + toMonth. Compute that in-memory after the
            // first-pass year range so the InMemory test provider can
            // execute it.
            var rawRows = await _db.PersonalAccountEntries
                .Where(e => e.PersonalAccountId == accountId.Value
                            && e.IsActive
                            && e.Year >= fromYear
                            && e.Year <= toYear)
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

            int fromIdx = (fromYear * 12) + fromMonthIdx;
            int toIdx = (toYear * 12) + toMonthIdx;
            months = rawRows
                .Where(r => (r.Year * 12) + r.Month >= fromIdx
                            && (r.Year * 12) + r.Month <= toIdx)
                .OrderBy(r => r.Year)
                .ThenBy(r => r.Month)
                .Select(r => new ContributionMonthEntryDto(
                    Year: r.Year,
                    Month: r.Month,
                    Base: r.ContributionBaseAmount,
                    Paid: r.ContributionPaidAmount,
                    Source: r.SourceCode))
                .ToList();
        }

        var total = months.Sum(m => m.Paid);
        var distinctMonths = months
            .Select(m => (m.Year, m.Month))
            .Distinct()
            .Count();

        var dto = new ContributionHistoryDto(
            IdnpHashPrefix: hashPrefix,
            Months: months,
            TotalContributionsInWindow: total,
            MonthsInWindow: distinctMonths);

        await WriteAuditAsync(
            AuditContributionHistory,
            JsonSerializer.Serialize(new
            {
                idnpHashPrefix = hashPrefix,
                monthsCount = months.Count,
                fromMonth,
                toMonth,
                asOfUtc = _clock.UtcNow,
            }),
            targetEntity: nameof(Solicitant),
            targetEntityId: solicitant.Id,
            ct).ConfigureAwait(false);

        return Result<ContributionHistoryDto>.Success(dto);
    }

    /// <inheritdoc />
    public async Task<Result<BenefitsListDto>> GetBenefitsListAsync(
        string idnp,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNameBenefitsList);

        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<BenefitsListDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        var hashFull = _hasher.ComputeHash(idnpResult.Value.Value);
        var hashPrefix = HashPrefix(hashFull);

        var solicitant = await _db.Solicitants
            .Where(s => s.NationalIdHash == hashFull && s.IsActive)
            .Select(s => new { s.Id })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (solicitant is null)
        {
            await WriteAuditAsync(
                AuditBenefitsList,
                JsonSerializer.Serialize(new
                {
                    idnpHashPrefix = hashPrefix,
                    isRegistered = false,
                    asOfUtc = _clock.UtcNow,
                }),
                targetEntity: null,
                targetEntityId: null,
                ct).ConfigureAwait(false);
            return Result<BenefitsListDto>.Failure(
                ErrorCodes.NotFound,
                "No active Solicitant on file for the supplied IDNP.");
        }

        // Materialise all payment rows for the Solicitant (active rows
        // only). Volumes are bounded by the per-citizen ledger depth
        // (decades of monthly entries × ≤8 benefit types) which is small
        // enough for a single round-trip. The in-memory grouping keeps the
        // LINQ tree friendly to the InMemory test provider.
        var rows = await _db.BenefitPayments
            .Where(b => b.BeneficiarySolicitantId == solicitant.Id && b.IsActive)
            .Select(b => new
            {
                b.BenefitType,
                b.PaymentMonth,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var benefits = rows
            .GroupBy(r => r.BenefitType)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => new BenefitEntryDto(
                Type: g.Key.ToString(),
                FirstPaymentMonth: g.Min(r => (DateOnly?)r.PaymentMonth),
                LastPaymentMonth: g.Max(r => (DateOnly?)r.PaymentMonth),
                TotalPaymentsCount: g.Count()))
            .ToList();

        var dto = new BenefitsListDto(
            IdnpHashPrefix: hashPrefix,
            Benefits: benefits);

        await WriteAuditAsync(
            AuditBenefitsList,
            JsonSerializer.Serialize(new
            {
                idnpHashPrefix = hashPrefix,
                benefitTypesCount = benefits.Count,
                asOfUtc = _clock.UtcNow,
            }),
            targetEntity: nameof(Solicitant),
            targetEntityId: solicitant.Id,
            ct).ConfigureAwait(false);

        return Result<BenefitsListDto>.Success(dto);
    }

    /// <inheritdoc />
    public async Task<Result<PersonalAccountSnapshotDto>> GetPersonalAccountSnapshotAsync(
        string idnp,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNamePersonalAccountSnapshot);

        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<PersonalAccountSnapshotDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        var hashFull = _hasher.ComputeHash(idnpResult.Value.Value);
        var hashPrefix = HashPrefix(hashFull);

        var solicitant = await _db.Solicitants
            .Where(s => s.NationalIdHash == hashFull && s.IsActive)
            .Select(s => new { s.Id })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (solicitant is null)
        {
            await WriteAuditAsync(
                AuditPersonalAccountSnapshot,
                JsonSerializer.Serialize(new
                {
                    idnpHashPrefix = hashPrefix,
                    isRegistered = false,
                    asOfUtc = _clock.UtcNow,
                }),
                targetEntity: null,
                targetEntityId: null,
                ct).ConfigureAwait(false);
            return Result<PersonalAccountSnapshotDto>.Failure(
                ErrorCodes.NotFound,
                "No active Solicitant on file for the supplied IDNP.");
        }

        var account = await _db.PersonalAccounts
            .Where(p => p.OwnerSolicitantId == solicitant.Id && p.IsActive)
            .Select(p => new
            {
                p.Id,
                p.AccountCode,
                p.LifetimeContributions,
                p.LifetimeMonths,
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (account is null)
        {
            await WriteAuditAsync(
                AuditPersonalAccountSnapshot,
                JsonSerializer.Serialize(new
                {
                    idnpHashPrefix = hashPrefix,
                    isRegistered = true,
                    hasAccount = false,
                    asOfUtc = _clock.UtcNow,
                }),
                targetEntity: nameof(Solicitant),
                targetEntityId: solicitant.Id,
                ct).ConfigureAwait(false);
            return Result<PersonalAccountSnapshotDto>.Failure(
                ErrorCodes.NotFound,
                "Solicitant has no personal-account aggregate on file.");
        }

        var dto = new PersonalAccountSnapshotDto(
            IdnpHashPrefix: hashPrefix,
            AccountCode: account.AccountCode,
            LifetimeContributions: account.LifetimeContributions,
            LifetimeMonths: account.LifetimeMonths,
            AsOfUtc: _clock.UtcNow);

        await WriteAuditAsync(
            AuditPersonalAccountSnapshot,
            JsonSerializer.Serialize(new
            {
                idnpHashPrefix = hashPrefix,
                accountId = account.Id,
                asOfUtc = dto.AsOfUtc,
            }),
            targetEntity: nameof(PersonalAccount),
            targetEntityId: account.Id,
            ct).ConfigureAwait(false);

        return Result<PersonalAccountSnapshotDto>.Success(dto);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Deterministic stub.</b> The first-class <c>BenefitDecision</c>
    /// aggregate is partial — the decision registry lands in a follow-up
    /// batch. For now the op deterministically returns an empty
    /// <c>Decisions</c> list for any registered citizen so the API surface,
    /// validators, audit, and metrics can be exercised end-to-end by B2B
    /// integration tests against a stable shape.
    /// </remarks>
    public async Task<Result<ActiveDecisionsDto>> GetActiveDecisionsAsync(
        string idnp,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNameActiveDecisions);

        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<ActiveDecisionsDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        var hashFull = _hasher.ComputeHash(idnpResult.Value.Value);
        var hashPrefix = HashPrefix(hashFull);

        var solicitant = await _db.Solicitants
            .Where(s => s.NationalIdHash == hashFull && s.IsActive)
            .Select(s => new { s.Id })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (solicitant is null)
        {
            await WriteAuditAsync(
                AuditActiveDecisions,
                JsonSerializer.Serialize(new
                {
                    idnpHashPrefix = hashPrefix,
                    isRegistered = false,
                    asOfUtc = _clock.UtcNow,
                }),
                targetEntity: null,
                targetEntityId: null,
                ct).ConfigureAwait(false);
            return Result<ActiveDecisionsDto>.Failure(
                ErrorCodes.NotFound,
                "No active Solicitant on file for the supplied IDNP.");
        }

        // Deterministic stub — the BenefitDecision aggregate lands in a
        // follow-up batch (TODO §11). Return an empty decisions list so
        // consumers can wire their integration tests against a stable
        // shape without us fabricating fake decisions.
        var dto = new ActiveDecisionsDto(
            IdnpHashPrefix: hashPrefix,
            Decisions: Array.Empty<ActiveDecisionEntryDto>(),
            AsOfUtc: _clock.UtcNow);

        await WriteAuditAsync(
            AuditActiveDecisions,
            JsonSerializer.Serialize(new
            {
                idnpHashPrefix = hashPrefix,
                decisionsCount = dto.Decisions.Count,
                asOfUtc = dto.AsOfUtc,
            }),
            targetEntity: nameof(Solicitant),
            targetEntityId: solicitant.Id,
            ct).ConfigureAwait(false);

        return Result<ActiveDecisionsDto>.Success(dto);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Deterministic stub.</b> Pending the <c>BenefitDecision</c>
    /// aggregate this op currently surfaces <c>NOT_FOUND</c> for every
    /// well-formed Sqid — the API surface, validators, audit, and metrics
    /// are exercised end-to-end so consumers can wire integration tests
    /// against a stable shape.
    /// </remarks>
    public async Task<Result<PaymentStatusDto>> GetPaymentStatusAsync(
        string decisionSqid,
        DateOnly period,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNamePaymentStatus);

        // 1. Sqid validation. An empty / null handle is a client bug —
        //    surface a stable error code instead of letting the decoder
        //    explode below.
        if (string.IsNullOrWhiteSpace(decisionSqid))
        {
            return Result<PaymentStatusDto>.Failure(
                ErrorCodes.InvalidSqid,
                "DecisionSqid is required.");
        }

        // 2. Period sanity — guard against epoch-zero / pathologically
        //    distant dates that may indicate a client bug.
        if (period.Year is < PaymentStatusRequestDtoValidator.MinSupportedYear
            or > PaymentStatusRequestDtoValidator.MaxSupportedYear)
        {
            return Result<PaymentStatusDto>.Failure(
                ErrorCodes.InvalidDateRange,
                "Period year is out of the supported range.");
        }

        // 3. Deterministic stub — the BenefitDecision aggregate lands in a
        //    follow-up batch. Audit the access then surface NOT_FOUND.
        await WriteAuditAsync(
            AuditPaymentStatus,
            JsonSerializer.Serialize(new
            {
                decisionSqid,
                period,
                outcome = "NOT_FOUND_STUB",
                asOfUtc = _clock.UtcNow,
            }),
            targetEntity: null,
            targetEntityId: null,
            ct).ConfigureAwait(false);

        return Result<PaymentStatusDto>.Failure(
            ErrorCodes.NotFound,
            "Decision not on file or no payment for the requested period.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Deterministic stub for legal entities.</b> The natural-person
    /// branch resolves against the existing <c>Solicitant</c> aggregate; the
    /// legal-entity branch returns <c>NOT_FOUND</c> until the <c>Payer</c>
    /// registry is wired into the read-only context (deferred).
    /// </remarks>
    public async Task<Result<PayerDataDto>> GetPayerDataAsync(
        string taxpayerCode,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNamePayerData);

        // 1. Shape validation. Both IDNP and IDNO are 13 numeric digits;
        //    accept anything that satisfies that shape and dispatch
        //    further below.
        if (string.IsNullOrWhiteSpace(taxpayerCode)
            || taxpayerCode.Length != PayerDataRequestDtoValidator.CodeLength
            || !taxpayerCode.All(char.IsDigit))
        {
            return Result<PayerDataDto>.Failure(
                ErrorCodes.ValidationFailed,
                "TaxpayerCode must be a 13-digit numeric string.");
        }

        // 2. Try the IDNP branch first (natural person). On a checksum
        //    failure we treat the input as an IDNO candidate and dispatch
        //    to the legal-entity branch.
        var idnpAttempt = Idnp.TryCreate(taxpayerCode);
        if (idnpAttempt.IsSuccess)
        {
            var hashFull = _hasher.ComputeHash(idnpAttempt.Value.Value);
            var hashPrefix = HashPrefix(hashFull);

            var solicitant = await _db.Solicitants
                .Where(s => s.NationalIdHash == hashFull && s.IsActive)
                .Select(s => new { s.Id, s.DisplayName, s.CreatedAtUtc })
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (solicitant is null)
            {
                await WriteAuditAsync(
                    AuditPayerData,
                    JsonSerializer.Serialize(new
                    {
                        taxpayerHashPrefix = hashPrefix,
                        payerKind = nameof(PayerKind.NaturalPerson),
                        outcome = "NOT_FOUND",
                        asOfUtc = _clock.UtcNow,
                    }),
                    targetEntity: null,
                    targetEntityId: null,
                    ct).ConfigureAwait(false);
                return Result<PayerDataDto>.Failure(
                    ErrorCodes.NotFound,
                    "No active payer on file for the supplied taxpayer code.");
            }

            var registrationDate = DateOnly.FromDateTime(solicitant.CreatedAtUtc);
            var dto = new PayerDataDto(
                TaxpayerHashPrefix: hashPrefix,
                PayerKind: nameof(PayerKind.NaturalPerson),
                DisplayName: solicitant.DisplayName,
                RegistrationDate: registrationDate,
                Status: nameof(PayerLifecycleStatus.Active),
                CountOfInsuredEmployees: 0,
                LastDeclarationMonth: null);

            await WriteAuditAsync(
                AuditPayerData,
                JsonSerializer.Serialize(new
                {
                    taxpayerHashPrefix = hashPrefix,
                    payerKind = nameof(PayerKind.NaturalPerson),
                    outcome = "FOUND",
                    asOfUtc = _clock.UtcNow,
                }),
                targetEntity: nameof(Solicitant),
                targetEntityId: solicitant.Id,
                ct).ConfigureAwait(false);
            return Result<PayerDataDto>.Success(dto);
        }

        // 3. IDNO branch — deterministic stub pending the Payer registry
        //    integration with the read-only context.
        var idnoAttempt = Idno.TryCreate(taxpayerCode);
        if (idnoAttempt.IsSuccess)
        {
            var idnoHashFull = _hasher.ComputeHash(idnoAttempt.Value.Value);
            var idnoHashPrefix = HashPrefix(idnoHashFull);

            await WriteAuditAsync(
                AuditPayerData,
                JsonSerializer.Serialize(new
                {
                    taxpayerHashPrefix = idnoHashPrefix,
                    payerKind = nameof(PayerKind.LegalEntity),
                    outcome = "NOT_FOUND_STUB",
                    asOfUtc = _clock.UtcNow,
                }),
                targetEntity: null,
                targetEntityId: null,
                ct).ConfigureAwait(false);
            return Result<PayerDataDto>.Failure(
                ErrorCodes.NotFound,
                "No active legal-entity payer on file for the supplied IDNO.");
        }

        // 4. Both branches rejected the code — surface ValidationFailed
        //    rather than the more specific InvalidIdnp / InvalidIdno so the
        //    caller does not see an ambiguous "this is malformed against
        //    branch X" message.
        return Result<PayerDataDto>.Failure(
            ErrorCodes.ValidationFailed,
            "TaxpayerCode is not a valid IDNP or IDNO.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Affirmative-branch wiring.</b> The implementation reuses the
    /// existing <c>BenefitPayment</c> aggregate to source an affirmative
    /// answer (any active payment of the probed kind in the last 12 months
    /// counts as a current beneficiary). The negative branch surfaces a
    /// machine-readable reason code so the consumer can switch on it.
    /// </remarks>
    public async Task<Result<IsBenefitBeneficiaryDto>> IsBenefitBeneficiaryAsync(
        string idnp,
        string benefitType,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNameIsBenefitBeneficiary);

        // 1. IDNP validation.
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<IsBenefitBeneficiaryDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        // 2. BenefitType parsing — accept only the stable enum-name string
        //    vocabulary. Unknown values surface as ValidationFailed.
        if (string.IsNullOrWhiteSpace(benefitType)
            || !Enum.TryParse<BenefitType>(benefitType, ignoreCase: false, out var parsedBenefit))
        {
            return Result<IsBenefitBeneficiaryDto>.Failure(
                ErrorCodes.ValidationFailed,
                "BenefitType is not a known benefit kind.");
        }

        var hashFull = _hasher.ComputeHash(idnpResult.Value.Value);
        var hashPrefix = HashPrefix(hashFull);
        var evaluationDate = DateOnly.FromDateTime(_clock.UtcNow);

        var solicitant = await _db.Solicitants
            .Where(s => s.NationalIdHash == hashFull && s.IsActive)
            .Select(s => new { s.Id })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        IsBenefitBeneficiaryDto dto;
        if (solicitant is null)
        {
            dto = new IsBenefitBeneficiaryDto(
                IdnpHashPrefix: hashPrefix,
                BenefitType: parsedBenefit.ToString(),
                IsBeneficiary: false,
                Reason: "UNKNOWN_IDNP",
                EvaluationDate: evaluationDate,
                DecisionSqid: null);
        }
        else
        {
            // Active beneficiary = at least one Paid row of the probed
            // benefit type with a PaymentMonth within the last 12 months
            // ending at the evaluation date. The 12-month window mirrors
            // the consumer-facing definition used by RSP / SIVE.
            var windowStart = evaluationDate.AddMonths(-12);
            var hasPaid = await _db.BenefitPayments
                .Where(b => b.BeneficiarySolicitantId == solicitant.Id
                            && b.IsActive
                            && b.BenefitType == parsedBenefit
                            && b.Status == BenefitPaymentStatus.Paid
                            && b.PaymentMonth >= windowStart
                            && b.PaymentMonth <= evaluationDate)
                .AnyAsync(ct)
                .ConfigureAwait(false);

            dto = new IsBenefitBeneficiaryDto(
                IdnpHashPrefix: hashPrefix,
                BenefitType: parsedBenefit.ToString(),
                IsBeneficiary: hasPaid,
                Reason: hasPaid ? string.Empty : "NO_ACTIVE_DECISION",
                EvaluationDate: evaluationDate,
                DecisionSqid: null);
        }

        await WriteAuditAsync(
            AuditIsBenefitBeneficiary,
            JsonSerializer.Serialize(new
            {
                idnpHashPrefix = hashPrefix,
                benefitType = parsedBenefit.ToString(),
                isBeneficiary = dto.IsBeneficiary,
                asOfUtc = _clock.UtcNow,
            }),
            targetEntity: solicitant is null ? null : nameof(Solicitant),
            targetEntityId: solicitant?.Id,
            ct).ConfigureAwait(false);

        return Result<IsBenefitBeneficiaryDto>.Success(dto);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Deterministic stub.</b> The legal-entity payer registry lands in
    /// a follow-up batch — the op returns <c>NOT_FOUND</c> for every
    /// well-formed IDNO until the <c>Payer</c> aggregate is integrated with
    /// the read-only context.
    /// </remarks>
    public async Task<Result<ContributionPaymentInfoDto>> GetContributionPaymentInfoAsync(
        string idno,
        DateOnly period,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNameContributionPaymentInfo);

        var idnoResult = Idno.TryCreate(idno);
        if (idnoResult.IsFailure)
        {
            return Result<ContributionPaymentInfoDto>.Failure(
                idnoResult.ErrorCode!,
                idnoResult.ErrorMessage!);
        }

        if (period.Year is < ContributionPaymentInfoRequestDtoValidator.MinSupportedYear
            or > ContributionPaymentInfoRequestDtoValidator.MaxSupportedYear)
        {
            return Result<ContributionPaymentInfoDto>.Failure(
                ErrorCodes.InvalidDateRange,
                "Period year is out of the supported range.");
        }

        var hashFull = _hasher.ComputeHash(idnoResult.Value.Value);
        var hashPrefix = HashPrefix(hashFull);

        await WriteAuditAsync(
            AuditContributionPaymentInfo,
            JsonSerializer.Serialize(new
            {
                idnoHashPrefix = hashPrefix,
                period,
                outcome = "NOT_FOUND_STUB",
                asOfUtc = _clock.UtcNow,
            }),
            targetEntity: null,
            targetEntityId: null,
            ct).ConfigureAwait(false);

        return Result<ContributionPaymentInfoDto>.Failure(
            ErrorCodes.NotFound,
            "No active legal-entity payer on file for the supplied IDNO.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Deterministic stub.</b> The bilateral-agreement registry lands in
    /// a follow-up batch — the op returns the
    /// <c>NotApplicable</c> branch for every well-formed (IDNP, code) tuple
    /// until the underlying registry is wired into the read-only context.
    /// The shape, audit row, and metrics are real so consumers can wire
    /// integration tests now.
    /// </remarks>
    public async Task<Result<LegalApplicableFormDto>> GetLegalApplicableFormAsync(
        string idnp,
        string agreementCode,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNameLegalApplicableForm);

        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<LegalApplicableFormDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        if (string.IsNullOrWhiteSpace(agreementCode)
            || agreementCode.Length is < LegalApplicableFormRequestDtoValidator.MinAgreementCodeLength
                                     or > LegalApplicableFormRequestDtoValidator.MaxAgreementCodeLength
            || !agreementCode.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return Result<LegalApplicableFormDto>.Failure(
                ErrorCodes.ValidationFailed,
                "AgreementCode is malformed.");
        }

        var hashFull = _hasher.ComputeHash(idnpResult.Value.Value);
        var hashPrefix = HashPrefix(hashFull);

        // Derive a best-effort ISO-3166 alpha-2 host-country prefix from
        // the agreement code (everything before the first underscore). This
        // is a stable convention: agreement codes are shaped
        // {ISO}_{MD}_{YEAR}. If the convention doesn't hold (defensive) we
        // fall back to "XX" — the consumer can detect the fallback.
        string hostCountryCode;
        var underscoreIdx = agreementCode.IndexOf('_', StringComparison.Ordinal);
        if (underscoreIdx == 2
            && char.IsLetter(agreementCode[0])
            && char.IsLetter(agreementCode[1]))
        {
            hostCountryCode = agreementCode[..2].ToUpperInvariant();
        }
        else
        {
            hostCountryCode = "XX";
        }

        var dto = new LegalApplicableFormDto(
            IdnpHashPrefix: hashPrefix,
            AgreementCode: agreementCode,
            ApplicableForm: nameof(LegalAgreementApplicableForm.NotApplicable),
            FormSerialNumber: null,
            IssueDate: null,
            ValidUntil: null,
            HostCountryCode: hostCountryCode);

        await WriteAuditAsync(
            AuditLegalApplicableForm,
            JsonSerializer.Serialize(new
            {
                idnpHashPrefix = hashPrefix,
                agreementCode,
                applicableForm = dto.ApplicableForm,
                asOfUtc = _clock.UtcNow,
            }),
            targetEntity: null,
            targetEntityId: null,
            ct).ConfigureAwait(false);

        return Result<LegalApplicableFormDto>.Success(dto);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolves the citizen via the deterministic IDNP hash, then projects
    /// the <c>PersonalAccountEntry</c> rows for the resolved account into a
    /// per-month set. <c>TotalMonths</c> is the cardinality of that set,
    /// <c>PeriodCount</c> is the number of continuous spells (gaps in the
    /// per-month set break a spell), and <c>CurrentlyInsured</c> is
    /// <c>true</c> iff at least one row covers the evaluation month.
    /// </remarks>
    public async Task<Result<WorkInsurancePeriodDto>> GetWorkInsurancePeriodAsync(
        string idnp,
        CancellationToken ct = default)
    {
        EmitOpInvoked(OpNameWorkInsurancePeriod);

        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<WorkInsurancePeriodDto>.Failure(
                idnpResult.ErrorCode!,
                idnpResult.ErrorMessage!);
        }

        var hashFull = _hasher.ComputeHash(idnpResult.Value.Value);
        var hashPrefix = HashPrefix(hashFull);

        var solicitant = await _db.Solicitants
            .Where(s => s.NationalIdHash == hashFull && s.IsActive)
            .Select(s => new { s.Id })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (solicitant is null)
        {
            await WriteAuditAsync(
                AuditWorkInsurancePeriod,
                JsonSerializer.Serialize(new
                {
                    idnpHashPrefix = hashPrefix,
                    isRegistered = false,
                    asOfUtc = _clock.UtcNow,
                }),
                targetEntity: null,
                targetEntityId: null,
                ct).ConfigureAwait(false);
            return Result<WorkInsurancePeriodDto>.Failure(
                ErrorCodes.NotFound,
                "No active Solicitant on file for the supplied IDNP.");
        }

        var accountId = await _db.PersonalAccounts
            .Where(p => p.OwnerSolicitantId == solicitant.Id && p.IsActive)
            .Select(p => (long?)p.Id)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        WorkInsurancePeriodDto dto;
        if (accountId is null)
        {
            dto = new WorkInsurancePeriodDto(
                IdnpHashPrefix: hashPrefix,
                TotalMonths: 0,
                FirstInsuredMonth: null,
                LastInsuredMonth: null,
                CurrentlyInsured: false,
                PeriodCount: 0);
        }
        else
        {
            var rows = await _db.PersonalAccountEntries
                .Where(e => e.PersonalAccountId == accountId.Value && e.IsActive)
                .Select(e => new { e.Year, e.Month })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // De-duplicate (Year, Month) tuples to compute the unique
            // insured-month count + the continuous-spell count.
            var orderedMonths = rows
                .Select(r => (r.Year * 12) + r.Month)
                .Distinct()
                .OrderBy(m => m)
                .ToList();

            int periodCount = 0;
            int? prev = null;
            foreach (var m in orderedMonths)
            {
                if (prev is null || m - prev.Value > 1)
                {
                    periodCount++;
                }
                prev = m;
            }

            DateOnly? firstMonth = orderedMonths.Count == 0
                ? null
                : new DateOnly(orderedMonths[0] / 12, orderedMonths[0] % 12 == 0 ? 12 : orderedMonths[0] % 12, 1);
            DateOnly? lastMonth = orderedMonths.Count == 0
                ? null
                : new DateOnly(
                    orderedMonths[^1] / 12,
                    orderedMonths[^1] % 12 == 0 ? 12 : orderedMonths[^1] % 12,
                    1);

            // Bucket boundaries: Year*12 + Month carries Month in [1..12], so
            // when (Y*12 + M) % 12 == 0 the calendar month is December of
            // (Y - 1) — the conditional in the projections above handles the
            // wrap. The same arithmetic recovers the encoded month below.
            if (firstMonth is not null)
            {
                int firstIdx = orderedMonths[0];
                int fm = firstIdx % 12;
                int fy = (firstIdx - (fm == 0 ? 12 : fm)) / 12;
                firstMonth = new DateOnly(fy, fm == 0 ? 12 : fm, 1);
            }
            if (lastMonth is not null)
            {
                int lastIdx = orderedMonths[^1];
                int lm = lastIdx % 12;
                int ly = (lastIdx - (lm == 0 ? 12 : lm)) / 12;
                lastMonth = new DateOnly(ly, lm == 0 ? 12 : lm, 1);
            }

            var today = DateOnly.FromDateTime(_clock.UtcNow);
            int todayIdx = (today.Year * 12) + today.Month;
            bool currentlyInsured = orderedMonths.Contains(todayIdx);

            dto = new WorkInsurancePeriodDto(
                IdnpHashPrefix: hashPrefix,
                TotalMonths: orderedMonths.Count,
                FirstInsuredMonth: firstMonth,
                LastInsuredMonth: lastMonth,
                CurrentlyInsured: currentlyInsured,
                PeriodCount: periodCount);
        }

        await WriteAuditAsync(
            AuditWorkInsurancePeriod,
            JsonSerializer.Serialize(new
            {
                idnpHashPrefix = hashPrefix,
                totalMonths = dto.TotalMonths,
                periodCount = dto.PeriodCount,
                asOfUtc = _clock.UtcNow,
            }),
            targetEntity: nameof(Solicitant),
            targetEntityId: solicitant.Id,
            ct).ConfigureAwait(false);

        return Result<WorkInsurancePeriodDto>.Success(dto);
    }

    /// <summary>
    /// Emits the per-op <c>cnas.interop.op_invoked</c> counter increment
    /// with the supplied <paramref name="opName"/> tag. Wrapped in a helper
    /// so every op routes through the same allocation pattern (a single
    /// <see cref="KeyValuePair{TKey, TValue}"/> on the stack).
    /// </summary>
    /// <param name="opName">Canonical Annex-4 op name (one of the OpNameXxx constants).</param>
    private static void EmitOpInvoked(string opName)
    {
        CnasMeter.InteropOpInvoked.Add(1, new KeyValuePair<string, object?>("op_name", opName));
    }

    /// <summary>
    /// Renders the first <see cref="IdnpHashPrefixLength"/> hex characters
    /// of the supplied deterministic hash. The deterministic hasher emits
    /// base64; we decode the first few bytes back to raw and re-emit them
    /// as lower-case hex so the response shape matches the Annex-4
    /// contract (<c>[0-9a-f]{8}</c>). Falls back to a deterministic
    /// SHA-style prefix when the input is shorter than expected, but in
    /// practice <see cref="IDeterministicHasher.ComputeHash"/> always
    /// returns a 44-char base64 string.
    /// </summary>
    /// <param name="fullHash">Full base64 hash from <see cref="IDeterministicHasher.ComputeHash"/>.</param>
    /// <returns>Exactly <see cref="IdnpHashPrefixLength"/> lower-case hex characters.</returns>
    private static string HashPrefix(string fullHash)
    {
        ArgumentNullException.ThrowIfNull(fullHash);

        // Decode the leading bytes of the base64 hash, then hex-encode just
        // enough of them to fill IdnpHashPrefixLength characters. 8 hex
        // chars = 4 raw bytes; we feed 8 base64 chars (= 6 raw bytes) into
        // the decoder so we have headroom against base64 padding.
        Span<byte> raw = stackalloc byte[6];
        var sliceLength = Math.Min(fullHash.Length, 8);
        if (Convert.TryFromBase64String(
            fullHash.AsSpan(0, sliceLength).ToString(),
            raw,
            out var written) && written >= IdnpHashPrefixLength / 2)
        {
            var sb = new StringBuilder(IdnpHashPrefixLength);
            for (int i = 0; i < IdnpHashPrefixLength / 2; i++)
            {
                sb.Append(raw[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // Fallback: hex-encode the UTF-16 bytes of the prefix. Stable and
        // deterministic — chosen only when the base64 decode unexpectedly
        // fails (no production code path triggers this).
        var fallbackPrefix = fullHash.Length >= IdnpHashPrefixLength
            ? fullHash[..IdnpHashPrefixLength]
            : fullHash.PadRight(IdnpHashPrefixLength, '0');
        var fallbackBytes = Encoding.UTF8.GetBytes(fallbackPrefix);
        var fb = new StringBuilder(IdnpHashPrefixLength);
        for (int i = 0; i < IdnpHashPrefixLength / 2 && i < fallbackBytes.Length; i++)
        {
            fb.Append(fallbackBytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        while (fb.Length < IdnpHashPrefixLength)
        {
            fb.Append('0');
        }
        return fb.ToString();
    }

    /// <summary>
    /// Emits one Sensitive audit row for the interop call. Anonymous /
    /// system callers fall back to the literal actor id <c>"interop"</c>
    /// since these endpoints run as a machine-to-machine surface — the
    /// real ClientCredentials subject will fill in here once the OAuth2
    /// binding lands (deferred).
    /// </summary>
    /// <param name="eventCode">Stable audit event code (one of the four AuditXxx constants).</param>
    /// <param name="detailsJson">Serialised audit payload — hash prefix + counters only.</param>
    /// <param name="targetEntity">Entity name when a Solicitant / PersonalAccount was resolved, <c>null</c> otherwise.</param>
    /// <param name="targetEntityId">Surrogate id of the resolved aggregate when known.</param>
    /// <param name="ct">Standard cancellation token.</param>
    private Task WriteAuditAsync(
        string eventCode,
        string detailsJson,
        string? targetEntity,
        long? targetEntityId,
        CancellationToken ct)
    {
        return _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Sensitive,
            actorId: _caller.UserSqid ?? "interop",
            targetEntity: targetEntity,
            targetEntityId: targetEntityId,
            detailsJson: detailsJson,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct);
    }
}
