using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Contributors;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Contributors;

/// <summary>
/// R0912 / TOR BP 2.2-C — concrete implementation of
/// <see cref="ISocialInsuranceContractService"/>. Owns the issue / modify /
/// terminate lifecycle for the
/// <see cref="ContributorSocialInsuranceContract"/> rows attached to an
/// InsuredPerson (Persoană asigurată).
/// </summary>
public sealed class SocialInsuranceContractService : ISocialInsuranceContractService
{
    /// <summary>Stable audit event code emitted on a successful issue.</summary>
    public const string AuditIssued = "CONTRACT.ISSUED";

    /// <summary>Stable audit event code emitted on a supersession modify.</summary>
    public const string AuditModified = "CONTRACT.MODIFIED";

    /// <summary>Stable audit event code emitted on a terminate.</summary>
    public const string AuditTerminated = "CONTRACT.TERMINATED";

    /// <summary>Stable failure message returned when the Contributor is deactivated.</summary>
    public const string ContributorDeactivatedMessage = "CONTRIBUTOR_DEACTIVATED";

    /// <summary>Stable failure message returned when an overlapping active contract exists.</summary>
    public const string OverlappingContractMessage = "OVERLAPPING_ACTIVE_CONTRACT";

    /// <summary>Stable failure message returned when the contract is already terminated.</summary>
    public const string AlreadyTerminatedMessage = "CONTRACT_ALREADY_TERMINATED";

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
    private readonly IValidator<SocialInsuranceContractIssueDto> _issueValidator;
    private readonly IValidator<SocialInsuranceContractModifyDto> _modifyValidator;
    private readonly IValidator<SocialInsuranceContractTerminateDto> _terminateValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="issueValidator">Validator for the issue-input shape.</param>
    /// <param name="modifyValidator">Validator for the modify-input shape.</param>
    /// <param name="terminateValidator">Validator for the terminate-input shape.</param>
    public SocialInsuranceContractService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<SocialInsuranceContractIssueDto> issueValidator,
        IValidator<SocialInsuranceContractModifyDto> modifyValidator,
        IValidator<SocialInsuranceContractTerminateDto> terminateValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(issueValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(terminateValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _issueValidator = issueValidator;
        _modifyValidator = modifyValidator;
        _terminateValidator = terminateValidator;
    }

    /// <inheritdoc />
    public async Task<Result<ContributorSocialInsuranceContractDto>> IssueAsync(
        SocialInsuranceContractIssueDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _issueValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ContributorSocialInsuranceContractDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.ContributorSqid);
        if (decoded.IsFailure)
        {
            return Result<ContributorSocialInsuranceContractDto>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var contributorId = decoded.Value;

        // The codebase models "Contributor" (Persoană asigurată) on the
        // InsuredPerson aggregate — see R0311 ContributorLinkedEntitiesService.
        var contributor = await _db.InsuredPersons
            .SingleOrDefaultAsync(p => p.Id == contributorId, ct).ConfigureAwait(false);
        if (contributor is null || !contributor.IsActive)
        {
            return Result<ContributorSocialInsuranceContractDto>.Failure(
                ErrorCodes.NotFound, "Contributor not found.");
        }

        // Reject overlapping active contract — a current row exists when
        // ValidToUtc is null AND (ContractEndDate is null OR > today).
        var today = DateOnly.FromDateTime(_clock.UtcNow);
        var existingCurrent = await _db.ContributorSocialInsuranceContracts
            .Where(c => c.ContributorId == contributorId
                && c.ValidToUtc == null
                && (c.ContractEndDate == null || c.ContractEndDate > today))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existingCurrent is not null)
        {
            return Result<ContributorSocialInsuranceContractDto>.Failure(
                ErrorCodes.Conflict, OverlappingContractMessage);
        }

        var now = _clock.UtcNow;
        var row = new ContributorSocialInsuranceContract
        {
            ContributorId = contributorId,
            ContractNumber = input.ContractNumber,
            ContractStartDate = input.ContractStartDate,
            ContractEndDate = input.ContractEndDate,
            MonthlyContributionAmount = input.MonthlyContributionAmount,
            CounterpartyName = input.CounterpartyName,
            ValidFromUtc = now,
            ChangeReason = input.ChangeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ContributorSocialInsuranceContracts.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RecordAuditAsync(AuditIssued, row.Id, new
        {
            contributorSqid = input.ContributorSqid,
            contractNumber = input.ContractNumber,
            startDate = input.ContractStartDate.ToString("O", CultureInfo.InvariantCulture),
            endDate = input.ContractEndDate?.ToString("O", CultureInfo.InvariantCulture),
        }, ct).ConfigureAwait(false);

        return Result<ContributorSocialInsuranceContractDto>.Success(ToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<ContributorSocialInsuranceContractDto>> ModifyAsync(
        long contractId,
        SocialInsuranceContractModifyDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _modifyValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ContributorSocialInsuranceContractDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var current = await _db.ContributorSocialInsuranceContracts
            .SingleOrDefaultAsync(c => c.Id == contractId, ct).ConfigureAwait(false);
        if (current is null)
        {
            return Result<ContributorSocialInsuranceContractDto>.Failure(
                ErrorCodes.NotFound, "Social-insurance contract not found.");
        }
        if (current.ValidToUtc is not null)
        {
            return Result<ContributorSocialInsuranceContractDto>.Failure(
                ErrorCodes.Conflict, AlreadyTerminatedMessage);
        }

        var now = _clock.UtcNow;

        // Close out the current row.
        current.ValidToUtc = now;
        current.UpdatedAtUtc = now;
        current.UpdatedBy = _caller.UserSqid;

        // Insert the modified successor row (R0311 supersession pattern).
        var successor = new ContributorSocialInsuranceContract
        {
            ContributorId = current.ContributorId,
            ContractNumber = input.ContractNumber,
            ContractStartDate = input.ContractStartDate ?? current.ContractStartDate,
            ContractEndDate = input.ContractEndDate ?? current.ContractEndDate,
            MonthlyContributionAmount = input.MonthlyContributionAmount ?? current.MonthlyContributionAmount,
            CounterpartyName = input.CounterpartyName ?? current.CounterpartyName,
            ValidFromUtc = now,
            ChangeReason = input.ChangeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.ContributorSocialInsuranceContracts.Add(successor);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RecordAuditAsync(AuditModified, successor.Id, new
        {
            contributorSqid = _sqids.Encode(current.ContributorId),
            previousContractSqid = _sqids.Encode(current.Id),
            newContractSqid = _sqids.Encode(successor.Id),
            contractNumber = input.ContractNumber,
            changeReason = input.ChangeReason,
        }, ct).ConfigureAwait(false);

        return Result<ContributorSocialInsuranceContractDto>.Success(ToDto(successor));
    }

    /// <inheritdoc />
    public async Task<Result> TerminateAsync(
        long contractId,
        DateOnly effectiveDate,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var input = new SocialInsuranceContractTerminateDto(effectiveDate, reason);
        var validation = await _terminateValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, validation.Errors[0].ErrorMessage);
        }

        var current = await _db.ContributorSocialInsuranceContracts
            .SingleOrDefaultAsync(c => c.Id == contractId, ct).ConfigureAwait(false);
        if (current is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Social-insurance contract not found.");
        }
        if (current.ValidToUtc is not null)
        {
            return Result.Failure(ErrorCodes.Conflict, AlreadyTerminatedMessage);
        }

        var now = _clock.UtcNow;
        current.ContractEndDate = effectiveDate;
        current.ValidToUtc = now;
        current.UpdatedAtUtc = now;
        current.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RecordAuditAsync(AuditTerminated, current.Id, new
        {
            contributorSqid = _sqids.Encode(current.ContributorId),
            contractNumber = current.ContractNumber,
            effectiveDate = effectiveDate.ToString("O", CultureInfo.InvariantCulture),
            reason,
        }, ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContributorSocialInsuranceContractDto>> GetCurrentForContributorAsync(
        long contributorId,
        CancellationToken ct = default)
    {
        var rows = await _db.ContributorSocialInsuranceContracts
            .Where(c => c.ContributorId == contributorId && c.ValidToUtc == null)
            .OrderByDescending(c => c.ValidFromUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        var dtos = new List<ContributorSocialInsuranceContractDto>(rows.Count);
        foreach (var r in rows)
        {
            dtos.Add(ToDto(r));
        }
        return dtos;
    }

    /// <summary>
    /// Emits a Critical-severity audit event with the supplied details
    /// payload serialised as JSON. Critical because contract lifecycle events
    /// materially alter the citizen's social-insurance status.
    /// </summary>
    /// <param name="eventCode">Stable audit event code.</param>
    /// <param name="targetId">Surrogate id of the affected contract row.</param>
    /// <param name="details">Anonymous payload object — serialised as JSON.</param>
    /// <param name="ct">Standard cancellation token.</param>
    private async Task RecordAuditAsync(
        string eventCode,
        long targetId,
        object details,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(details, CachedJsonOptions);
        await _audit.RecordAsync(
            eventCode,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(ContributorSocialInsuranceContract),
            targetId,
            json,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);
    }

    /// <summary>Projects an entity into its outbound DTO with Sqid-encoded ids.</summary>
    /// <param name="r">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private ContributorSocialInsuranceContractDto ToDto(ContributorSocialInsuranceContract r) => new(
        Id: _sqids.Encode(r.Id),
        ContributorSqid: _sqids.Encode(r.ContributorId),
        ContractNumber: r.ContractNumber,
        ContractStartDate: r.ContractStartDate,
        ContractEndDate: r.ContractEndDate,
        MonthlyContributionAmount: r.MonthlyContributionAmount,
        CounterpartyName: r.CounterpartyName,
        ValidFromUtc: r.ValidFromUtc,
        ValidToUtc: r.ValidToUtc,
        ChangeReason: r.ChangeReason,
        RecordedByUserSqid: r.RecordedByUserSqid);
}
