using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
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
/// R0913 / TOR BP 2.2-D — concrete implementation of
/// <see cref="IInsuredPersonAdjustmentService"/>. Owns the per-insured-person
/// contribution-adjustment create path (from non-REV-5 supporting documents)
/// and the projection into <see cref="PersonalAccountEntry"/>.
/// </summary>
public sealed class InsuredPersonAdjustmentService : IInsuredPersonAdjustmentService
{
    /// <summary>Stable audit event code emitted on a successful create.</summary>
    public const string AuditCreated = "INSURED_PERSON.CONTRIBUTION_ADJUSTED";

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
    private readonly IValidator<InsuredPersonContributionAdjustmentInputDto> _validator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="validator">Validator for the create-input shape.</param>
    public InsuredPersonAdjustmentService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IValidator<InsuredPersonContributionAdjustmentInputDto> validator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(validator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<InsuredPersonContributionAdjustmentDto>> CreateAsync(
        InsuredPersonContributionAdjustmentInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<InsuredPersonContributionAdjustmentDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.InsuredPersonSolicitantSqid);
        if (decoded.IsFailure)
        {
            return Result<InsuredPersonContributionAdjustmentDto>.Failure(
                decoded.ErrorCode!,
                decoded.ErrorMessage!);
        }
        var solicitantId = decoded.Value;

        var solicitantExists = await _db.Solicitants
            .AnyAsync(s => s.Id == solicitantId && s.IsActive, ct)
            .ConfigureAwait(false);
        if (!solicitantExists)
        {
            return Result<InsuredPersonContributionAdjustmentDto>.Failure(
                ErrorCodes.NotFound,
                "Insured-person Solicitant not found.");
        }

        var now = _clock.UtcNow;
        var adjustment = new InsuredPersonContributionAdjustment
        {
            InsuredPersonSolicitantId = solicitantId,
            Month = input.Month,
            AdjustmentAmount = input.AdjustmentAmount,
            SourceDocumentCode = input.SourceDocumentCode,
            SourceDocumentReference = input.SourceDocumentReference,
            Reason = input.Reason,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.InsuredPersonContributionAdjustments.Add(adjustment);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Project into PersonalAccountEntry when the Solicitant has a
        // personal account on file. No account = adjustment is recorded but
        // not projected (operator follow-up surfaced via audit + metrics).
        var accountId = await _db.PersonalAccounts
            .Where(p => p.OwnerSolicitantId == solicitantId && p.IsActive)
            .Select(p => (long?)p.Id)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (accountId is not null)
        {
            var existing = await _db.PersonalAccountEntries
                .SingleOrDefaultAsync(
                    e => e.PersonalAccountId == accountId.Value &&
                         e.Year == input.Month.Year &&
                         e.Month == input.Month.Month &&
                         e.SourceCode == input.SourceDocumentCode,
                    ct)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                existing.ContributionBaseAmount += input.AdjustmentAmount;
                existing.ContributionPaidAmount += input.AdjustmentAmount;
                existing.IsActive = true;
                existing.UpdatedAtUtc = now;
                existing.UpdatedBy = _caller.UserSqid;
            }
            else
            {
                _db.PersonalAccountEntries.Add(new PersonalAccountEntry
                {
                    PersonalAccountId = accountId.Value,
                    Year = input.Month.Year,
                    Month = input.Month.Month,
                    ContributionBaseAmount = input.AdjustmentAmount,
                    ContributionPaidAmount = input.AdjustmentAmount,
                    SourceCode = input.SourceDocumentCode,
                    CreatedAtUtc = now,
                    CreatedBy = _caller.UserSqid,
                    IsActive = true,
                });
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var details = JsonSerializer.Serialize(
            new
            {
                adjustmentSqid = _sqids.Encode(adjustment.Id),
                insuredPersonSolicitantSqid = input.InsuredPersonSolicitantSqid,
                month = input.Month.ToString("O", CultureInfo.InvariantCulture),
                input.AdjustmentAmount,
                input.SourceDocumentCode,
                input.SourceDocumentReference,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCreated,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(InsuredPersonContributionAdjustment),
            adjustment.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.InsuredPersonAdjustmentApplied.Add(
            1,
            new KeyValuePair<string, object?>("source_document_code", input.SourceDocumentCode));

        return Result<InsuredPersonContributionAdjustmentDto>.Success(ToDto(adjustment));
    }

    /// <summary>Projects an <see cref="InsuredPersonContributionAdjustment"/> entity into its outbound DTO.</summary>
    /// <param name="entity">Loaded entity.</param>
    /// <returns>Populated DTO with Sqid-encoded ids.</returns>
    private InsuredPersonContributionAdjustmentDto ToDto(InsuredPersonContributionAdjustment entity) => new(
        Id: _sqids.Encode(entity.Id),
        InsuredPersonSolicitantSqid: _sqids.Encode(entity.InsuredPersonSolicitantId),
        Month: entity.Month,
        AdjustmentAmount: entity.AdjustmentAmount,
        SourceDocumentCode: entity.SourceDocumentCode,
        SourceDocumentReference: entity.SourceDocumentReference,
        Reason: entity.Reason);
}
