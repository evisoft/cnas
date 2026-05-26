using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0311 / ARH 028 — concrete implementation of <see cref="IContributorLinkedEntitiesService"/>
/// owning supersession-based mutations on the six InsuredPerson (Persoană asigurată) child tables.
/// </summary>
public sealed class ContributorLinkedEntitiesService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    ISqidService sqids,
    ICallerContext caller,
    IAuditService audit) : IContributorLinkedEntitiesService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ISqidService _sqids = sqids;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;

    private const string EvtAddress = "CONTRIBUTORADDRESS.UPDATED";
    private const string EvtContact = "CONTRIBUTORCONTACT.UPDATED";
    private const string EvtActivity = "CONTRIBUTORACTIVITYPERIOD.ADDED";
    private const string EvtActivityEnded = "CONTRIBUTORACTIVITYPERIOD.ENDED";
    private const string EvtCivilStatus = "CONTRIBUTORCIVILSTATUS.UPDATED";
    private const string EvtContract = "CONTRIBUTORSOCIALINSURANCECONTRACT.UPDATED";
    private const string EvtPre1999 = "CONTRIBUTORPRE1999PERIOD.ADDED";

    /// <inheritdoc />
    public async Task<Result<ContributorAddressDto>> UpdateAddressAsync(
        long contributorId, ContributorAddressInputDto input, string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var current = await _db.ContributorAddresses
            .Where(a => a.ContributorId == contributorId && a.ValidToUtc == null)
            .OrderByDescending(a => a.ValidFromUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (current is not null && AddressEquals(current, input))
        {
            return Result<ContributorAddressDto>.Success(ToAddressDto(current));
        }

        var now = _clock.UtcNow;
        if (current is not null)
        {
            current.ValidToUtc = now;
            current.UpdatedAtUtc = now;
            current.UpdatedBy = _caller.UserSqid;
        }

        var row = new ContributorAddress
        {
            ContributorId = contributorId,
            Street = input.Street,
            City = input.City,
            Region = input.Region,
            PostalCode = input.PostalCode,
            Country = string.IsNullOrEmpty(input.Country) ? "MD" : input.Country,
            ValidFromUtc = now,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ContributorAddresses.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await EmitAuditAsync(EvtAddress, contributorId,
            AddressHash(current), AddressHash(row), changeReason, AuditSeverity.Notice, ct)
            .ConfigureAwait(false);
        return Result<ContributorAddressDto>.Success(ToAddressDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<ContributorContactDto>> UpdateContactAsync(
        long contributorId, ContributorContactInputDto input, string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var current = await _db.ContributorContacts
            .Where(c => c.ContributorId == contributorId && c.ValidToUtc == null)
            .OrderByDescending(c => c.ValidFromUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (current is not null && ContactEquals(current, input))
        {
            return Result<ContributorContactDto>.Success(ToContactDto(current));
        }

        var now = _clock.UtcNow;
        if (current is not null)
        {
            current.ValidToUtc = now;
            current.UpdatedAtUtc = now;
            current.UpdatedBy = _caller.UserSqid;
        }

        var row = new ContributorContact
        {
            ContributorId = contributorId,
            PhoneE164 = input.PhoneE164,
            Email = input.Email,
            ContactPersonName = input.ContactPersonName,
            ValidFromUtc = now,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ContributorContacts.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await EmitAuditAsync(EvtContact, contributorId,
            ContactHash(current), ContactHash(row), changeReason, AuditSeverity.Sensitive, ct)
            .ConfigureAwait(false);
        return Result<ContributorContactDto>.Success(ToContactDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<ContributorActivityPeriodDto>> AddActivityPeriodAsync(
        long contributorId, ContributorActivityPeriodInputDto input, string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.MonthlySalary.HasValue && input.MonthlySalary.Value < 0m)
        {
            return Result<ContributorActivityPeriodDto>.Failure(
                ErrorCodes.ValidationFailed, "MonthlySalary must be >= 0.");
        }

        var now = _clock.UtcNow;
        var row = new ContributorActivityPeriod
        {
            ContributorId = contributorId,
            EmployerCode = input.EmployerCode,
            Position = input.Position,
            MonthlySalary = input.MonthlySalary,
            ValidFromUtc = now,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ContributorActivityPeriods.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await EmitAuditAsync(EvtActivity, contributorId,
            null, $"employer:{input.EmployerCode}", changeReason, AuditSeverity.Notice, ct)
            .ConfigureAwait(false);
        return Result<ContributorActivityPeriodDto>.Success(ToActivityDto(row));
    }

    /// <inheritdoc />
    public async Task<Result> EndActivityPeriodAsync(
        long activityPeriodId, string? changeReason, CancellationToken ct = default)
    {
        var row = await _db.ContributorActivityPeriods
            .FirstOrDefaultAsync(p => p.Id == activityPeriodId && p.ValidToUtc == null, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Activity period not found or already ended.");
        }
        var now = _clock.UtcNow;
        row.ValidToUtc = now;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        if (!string.IsNullOrWhiteSpace(changeReason))
        {
            row.ChangeReason = changeReason;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await EmitAuditAsync(EvtActivityEnded, row.ContributorId,
            $"employer:{row.EmployerCode}", null, changeReason, AuditSeverity.Notice, ct)
            .ConfigureAwait(false);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<ContributorCivilStatusDto>> UpdateCivilStatusAsync(
        long contributorId, ContributorCivilStatusInputDto input, string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.TryParse<CivilStatusType>(input.Status, ignoreCase: true, out var parsed))
        {
            return Result<ContributorCivilStatusDto>.Failure(
                ErrorCodes.ValidationFailed, $"Unknown civil status '{input.Status}'.");
        }

        var current = await _db.ContributorCivilStatuses
            .Where(c => c.ContributorId == contributorId && c.ValidToUtc == null)
            .OrderByDescending(c => c.ValidFromUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (current is not null && current.Status == parsed
            && Nullable.Equals(current.EffectiveDate, input.EffectiveDate))
        {
            return Result<ContributorCivilStatusDto>.Success(ToCivilStatusDto(current));
        }

        var now = _clock.UtcNow;
        if (current is not null)
        {
            current.ValidToUtc = now;
            current.UpdatedAtUtc = now;
            current.UpdatedBy = _caller.UserSqid;
        }

        var row = new ContributorCivilStatus
        {
            ContributorId = contributorId,
            Status = parsed,
            EffectiveDate = input.EffectiveDate,
            ValidFromUtc = now,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ContributorCivilStatuses.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await EmitAuditAsync(EvtCivilStatus, contributorId,
            current?.Status.ToString(), parsed.ToString(), changeReason, AuditSeverity.Notice, ct)
            .ConfigureAwait(false);
        return Result<ContributorCivilStatusDto>.Success(ToCivilStatusDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<ContributorSocialInsuranceContractDto>> UpdateSocialInsuranceContractAsync(
        long contributorId, ContributorSocialInsuranceContractInputDto input, string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ContractEndDate is { } end && end <= input.ContractStartDate)
        {
            return Result<ContributorSocialInsuranceContractDto>.Failure(
                ErrorCodes.ValidationFailed, "ContractEndDate must be strictly after ContractStartDate.");
        }
        if (input.MonthlyContributionAmount < 0m || input.MonthlyContributionAmount > 1_000_000m)
        {
            return Result<ContributorSocialInsuranceContractDto>.Failure(
                ErrorCodes.ValidationFailed, "MonthlyContributionAmount must be within [0, 1_000_000].");
        }

        var current = await _db.ContributorSocialInsuranceContracts
            .Where(c => c.ContributorId == contributorId && c.ValidToUtc == null)
            .OrderByDescending(c => c.ValidFromUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (current is not null && ContractEquals(current, input))
        {
            return Result<ContributorSocialInsuranceContractDto>.Success(ToContractDto(current));
        }

        var now = _clock.UtcNow;
        if (current is not null)
        {
            current.ValidToUtc = now;
            current.UpdatedAtUtc = now;
            current.UpdatedBy = _caller.UserSqid;
        }

        var row = new ContributorSocialInsuranceContract
        {
            ContributorId = contributorId,
            ContractNumber = input.ContractNumber,
            ContractStartDate = input.ContractStartDate,
            ContractEndDate = input.ContractEndDate,
            MonthlyContributionAmount = input.MonthlyContributionAmount,
            CounterpartyName = input.CounterpartyName,
            ValidFromUtc = now,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ContributorSocialInsuranceContracts.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await EmitAuditAsync(EvtContract, contributorId,
            current?.ContractNumber, input.ContractNumber, changeReason, AuditSeverity.Notice, ct)
            .ConfigureAwait(false);
        return Result<ContributorSocialInsuranceContractDto>.Success(ToContractDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<ContributorPre1999PeriodCarnetMuncaDto>> AddPre1999PeriodAsync(
        long contributorId, ContributorPre1999PeriodCarnetMuncaInputDto input, string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.PeriodEndDate < input.PeriodStartDate)
        {
            return Result<ContributorPre1999PeriodCarnetMuncaDto>.Failure(
                ErrorCodes.ValidationFailed, "PeriodEndDate must be >= PeriodStartDate.");
        }

        var now = _clock.UtcNow;
        var row = new ContributorPre1999PeriodCarnetMunca
        {
            ContributorId = contributorId,
            CarnetMuncaNumber = input.CarnetMuncaNumber,
            PeriodStartDate = input.PeriodStartDate,
            PeriodEndDate = input.PeriodEndDate,
            EmployerName = input.EmployerName,
            Position = input.Position,
            ValidFromUtc = now,
            ChangeReason = changeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ContributorPre1999PeriodsCarnetMunca.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await EmitAuditAsync(EvtPre1999, contributorId,
            null, $"carnet:{input.CarnetMuncaNumber}", changeReason, AuditSeverity.Notice, ct)
            .ConfigureAwait(false);
        return Result<ContributorPre1999PeriodCarnetMuncaDto>.Success(ToPre1999Dto(row));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ContributorAddressDto>>> ListAddressHistoryAsync(
        long contributorId, CancellationToken ct = default)
    {
        var rows = await _db.ContributorAddresses
            .Where(a => a.ContributorId == contributorId)
            .OrderByDescending(a => a.ValidFromUtc).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ContributorAddressDto>>.Success(rows.Select(ToAddressDto).ToList());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ContributorContactDto>>> ListContactHistoryAsync(
        long contributorId, CancellationToken ct = default)
    {
        var rows = await _db.ContributorContacts
            .Where(c => c.ContributorId == contributorId)
            .OrderByDescending(c => c.ValidFromUtc).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ContributorContactDto>>.Success(rows.Select(ToContactDto).ToList());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ContributorActivityPeriodDto>>> ListActivityPeriodHistoryAsync(
        long contributorId, CancellationToken ct = default)
    {
        var rows = await _db.ContributorActivityPeriods
            .Where(c => c.ContributorId == contributorId)
            .OrderByDescending(c => c.ValidFromUtc).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ContributorActivityPeriodDto>>.Success(rows.Select(ToActivityDto).ToList());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ContributorCivilStatusDto>>> ListCivilStatusHistoryAsync(
        long contributorId, CancellationToken ct = default)
    {
        var rows = await _db.ContributorCivilStatuses
            .Where(c => c.ContributorId == contributorId)
            .OrderByDescending(c => c.ValidFromUtc).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ContributorCivilStatusDto>>.Success(rows.Select(ToCivilStatusDto).ToList());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ContributorSocialInsuranceContractDto>>> ListSocialInsuranceContractHistoryAsync(
        long contributorId, CancellationToken ct = default)
    {
        var rows = await _db.ContributorSocialInsuranceContracts
            .Where(c => c.ContributorId == contributorId)
            .OrderByDescending(c => c.ValidFromUtc).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ContributorSocialInsuranceContractDto>>.Success(rows.Select(ToContractDto).ToList());
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ContributorPre1999PeriodCarnetMuncaDto>>> ListPre1999PeriodsAsync(
        long contributorId, CancellationToken ct = default)
    {
        var rows = await _db.ContributorPre1999PeriodsCarnetMunca
            .Where(c => c.ContributorId == contributorId)
            .OrderByDescending(c => c.PeriodStartDate).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ContributorPre1999PeriodCarnetMuncaDto>>.Success(rows.Select(ToPre1999Dto).ToList());
    }

    // ─── helpers ─────────────────────

    private static bool AddressEquals(ContributorAddress row, ContributorAddressInputDto input) =>
        string.Equals(row.Street, input.Street, StringComparison.Ordinal)
        && string.Equals(row.City, input.City, StringComparison.Ordinal)
        && string.Equals(row.Region, input.Region, StringComparison.Ordinal)
        && string.Equals(row.PostalCode, input.PostalCode, StringComparison.Ordinal)
        && string.Equals(row.Country, input.Country, StringComparison.Ordinal);

    private static bool ContactEquals(ContributorContact row, ContributorContactInputDto input) =>
        string.Equals(row.PhoneE164, input.PhoneE164, StringComparison.Ordinal)
        && string.Equals(row.Email, input.Email, StringComparison.Ordinal)
        && string.Equals(row.ContactPersonName, input.ContactPersonName, StringComparison.Ordinal);

    private static bool ContractEquals(
        ContributorSocialInsuranceContract row, ContributorSocialInsuranceContractInputDto input) =>
        string.Equals(row.ContractNumber, input.ContractNumber, StringComparison.Ordinal)
        && row.ContractStartDate == input.ContractStartDate
        && Nullable.Equals(row.ContractEndDate, input.ContractEndDate)
        && row.MonthlyContributionAmount == input.MonthlyContributionAmount
        && string.Equals(row.CounterpartyName, input.CounterpartyName, StringComparison.Ordinal);

    private static string? AddressHash(ContributorAddress? r) => r is null
        ? null
        : Sha256Hex(string.Join('|', r.Street, r.City, r.Region, r.PostalCode, r.Country));

    private static string? ContactHash(ContributorContact? r) => r is null
        ? null
        : Sha256Hex(string.Join('|', r.PhoneE164 ?? "", r.Email ?? "", r.ContactPersonName ?? ""));

    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    private async Task EmitAuditAsync(
        string eventCode, long contributorId, string? fromHash, string? toHash,
        string? changeReason, AuditSeverity severity, CancellationToken ct)
    {
        var detail = JsonSerializer.Serialize(new
        {
            parentSqid = _sqids.Encode(contributorId),
            fromValuesHash = fromHash,
            toValuesHash = toHash,
            changeReason,
        });
        await _audit.RecordAsync(
            eventCode, severity, _caller.UserSqid ?? "system",
            "ContributorLinkedEntity", contributorId, detail,
            _caller.SourceIp, _caller.CorrelationId, ct).ConfigureAwait(false);
    }

    private ContributorAddressDto ToAddressDto(ContributorAddress r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.ContributorId),
        r.Street, r.City, r.Region, r.PostalCode, r.Country,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    private ContributorContactDto ToContactDto(ContributorContact r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.ContributorId),
        r.PhoneE164, r.Email, r.ContactPersonName,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    private ContributorActivityPeriodDto ToActivityDto(ContributorActivityPeriod r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.ContributorId),
        r.EmployerCode, r.Position, r.MonthlySalary,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    private ContributorCivilStatusDto ToCivilStatusDto(ContributorCivilStatus r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.ContributorId),
        r.Status.ToString(), r.EffectiveDate,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    private ContributorSocialInsuranceContractDto ToContractDto(ContributorSocialInsuranceContract r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.ContributorId),
        r.ContractNumber, r.ContractStartDate, r.ContractEndDate, r.MonthlyContributionAmount,
        r.CounterpartyName, r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);

    private ContributorPre1999PeriodCarnetMuncaDto ToPre1999Dto(ContributorPre1999PeriodCarnetMunca r) => new(
        _sqids.Encode(r.Id), _sqids.Encode(r.ContributorId),
        r.CarnetMuncaNumber, r.PeriodStartDate, r.PeriodEndDate, r.EmployerName, r.Position,
        r.ValidFromUtc, r.ValidToUtc, r.ChangeReason, r.RecordedByUserSqid);
}
