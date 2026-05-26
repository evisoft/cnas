using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Application.LaborBooklet;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.LaborBooklet;

/// <summary>
/// R0920 / R0921 / TOR BP 2.3 — concrete implementation of
/// <see cref="ILaborBookletService"/>. Owns the labor-booklet master-record
/// lifecycle plus the pre-01.01.1999 activity-period child operations.
/// </summary>
public sealed class LaborBookletService : ILaborBookletService
{
    /// <summary>Stable audit event code emitted by <see cref="RegisterAsync"/>.</summary>
    public const string AuditRegistered = "LABOR_BOOKLET.REGISTERED";

    /// <summary>Stable audit event code emitted by <see cref="AttachScannedCopyAsync"/>.</summary>
    public const string AuditScannedCopyAttached = "LABOR_BOOKLET.SCANNED_COPY_ATTACHED";

    /// <summary>Stable audit event code emitted by <see cref="VerifyAsync"/>.</summary>
    public const string AuditVerified = "LABOR_BOOKLET.VERIFIED";

    /// <summary>Stable audit event code emitted by <see cref="RejectAsync"/>.</summary>
    public const string AuditRejected = "LABOR_BOOKLET.REJECTED";

    /// <summary>Stable audit event code emitted by <see cref="AddPeriodAsync"/>.</summary>
    public const string AuditPeriodAdded = "PRE1999_PERIOD.ADDED";

    /// <summary>Stable audit event code emitted by <see cref="AmendPeriodAsync"/>.</summary>
    public const string AuditPeriodAmended = "PRE1999_PERIOD.AMENDED";

    /// <summary>Stable audit event code emitted by <see cref="ClosePeriodAsync"/>.</summary>
    public const string AuditPeriodClosed = "PRE1999_PERIOD.CLOSED";

    /// <summary>
    /// Stable failure message attached to the
    /// <see cref="ErrorCodes.Conflict"/> result when the per-citizen booklet-
    /// number uniqueness rule would reject the insert.
    /// </summary>
    public const string DuplicateMessage = "LABOR_BOOKLET_DUPLICATE";

    /// <summary>Cached JSON serializer options — reused across audit payload builders.</summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ICallerContext _caller;
    private readonly IAuditService _audit;
    private readonly IAttachmentService _attachments;
    private readonly IValidator<LaborBookletRegisterInputDto> _registerValidator;
    private readonly IValidator<LaborBookletVerifyInputDto> _verifyValidator;
    private readonly IValidator<LaborBookletRejectInputDto> _rejectValidator;
    private readonly IValidator<ScannedCopyAttachmentInputDto> _scannedValidator;
    private readonly IValidator<InsuredPersonPre1999PeriodInputDto> _periodValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="attachments">Attachment service used by <see cref="AttachScannedCopyAsync"/>.</param>
    /// <param name="registerValidator">Validator for the register-input shape.</param>
    /// <param name="verifyValidator">Validator for the verify-input shape.</param>
    /// <param name="rejectValidator">Validator for the reject-input shape.</param>
    /// <param name="scannedValidator">Validator for the scanned-copy upload envelope.</param>
    /// <param name="periodValidator">Validator for the pre-1999 period input shape.</param>
    public LaborBookletService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IAttachmentService attachments,
        IValidator<LaborBookletRegisterInputDto> registerValidator,
        IValidator<LaborBookletVerifyInputDto> verifyValidator,
        IValidator<LaborBookletRejectInputDto> rejectValidator,
        IValidator<ScannedCopyAttachmentInputDto> scannedValidator,
        IValidator<InsuredPersonPre1999PeriodInputDto> periodValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(registerValidator);
        ArgumentNullException.ThrowIfNull(verifyValidator);
        ArgumentNullException.ThrowIfNull(rejectValidator);
        ArgumentNullException.ThrowIfNull(scannedValidator);
        ArgumentNullException.ThrowIfNull(periodValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _attachments = attachments;
        _registerValidator = registerValidator;
        _verifyValidator = verifyValidator;
        _rejectValidator = rejectValidator;
        _scannedValidator = scannedValidator;
        _periodValidator = periodValidator;
    }

    /// <inheritdoc />
    public async Task<Result<LaborBookletDto>> RegisterAsync(
        LaborBookletRegisterInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _registerValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<LaborBookletDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.InsuredPersonSqid);
        if (decoded.IsFailure)
        {
            return Result<LaborBookletDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var solicitantId = decoded.Value;

        // Defensive existence check on the owning natural-person Solicitant.
        var solicitantExists = await _db.Solicitants
            .AnyAsync(s => s.Id == solicitantId && s.IsActive, ct)
            .ConfigureAwait(false);
        if (!solicitantExists)
        {
            return Result<LaborBookletDto>.Failure(ErrorCodes.NotFound, "Insured person Solicitant not found.");
        }

        // Per-citizen duplicate-key guard.
        var duplicate = await _db.LaborBooklets.AnyAsync(b =>
            b.InsuredPersonSolicitantId == solicitantId &&
            b.CarnetMuncaNumber == input.CarnetMuncaNumber &&
            b.IsActive,
            ct).ConfigureAwait(false);
        if (duplicate)
        {
            return Result<LaborBookletDto>.Failure(ErrorCodes.Conflict, DuplicateMessage);
        }

        var now = _clock.UtcNow;
        var entity = new Cnas.Ps.Core.Domain.LaborBooklet
        {
            InsuredPersonSolicitantId = solicitantId,
            CarnetMuncaNumber = input.CarnetMuncaNumber,
            IssuedDate = input.IssuedDate,
            IssuingAuthority = input.IssuingAuthority,
            Status = LaborBookletStatus.Pending,
            VerifierNotes = input.Notes,
            HasScannedCopy = false,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.LaborBooklets.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                laborBookletSqid = _sqids.Encode(entity.Id),
                insuredPersonSqid = input.InsuredPersonSqid,
                carnetMuncaNumber = input.CarnetMuncaNumber,
                issuedDate = input.IssuedDate?.ToString("O", CultureInfo.InvariantCulture),
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditRegistered,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Cnas.Ps.Core.Domain.LaborBooklet),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.LaborBookletRegistered.Add(1);

        return Result<LaborBookletDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<LaborBookletDto>> AttachScannedCopyAsync(
        long bookletId,
        ScannedCopyAttachmentInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _scannedValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<LaborBookletDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var entity = await _db.LaborBooklets
            .SingleOrDefaultAsync(b => b.Id == bookletId && b.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<LaborBookletDto>.Failure(ErrorCodes.NotFound, "Labor booklet not found.");
        }
        if (entity.Status == LaborBookletStatus.Rejected)
        {
            return Result<LaborBookletDto>.Failure(
                ErrorCodes.Conflict,
                "Cannot attach a scanned copy to a rejected labor booklet.");
        }

        var uploadInput = new AttachmentUploadDto(
            OwnerEntityType: AttachmentOwnerTypes.LaborBooklet,
            OwnerSqid: _sqids.Encode(entity.Id),
            ContentBase64: input.FileBase64,
            DeclaredFileName: input.FileName,
            Category: nameof(AttachmentCategory.LegalDocument),
            SensitivityLabel: nameof(Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential),
            Description: null);
        var uploadResult = await _attachments.UploadAsync(uploadInput, ct).ConfigureAwait(false);
        if (uploadResult.IsFailure)
        {
            return Result<LaborBookletDto>.Failure(uploadResult.ErrorCode!, uploadResult.ErrorMessage!);
        }

        var now = _clock.UtcNow;
        entity.HasScannedCopy = true;
        entity.OcrExtractedJson = input.OcrExtractedJson ?? entity.OcrExtractedJson;
        entity.OcrConfidenceLevel = input.OcrConfidenceLevel ?? entity.OcrConfidenceLevel;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                laborBookletSqid = _sqids.Encode(entity.Id),
                attachmentSqid = uploadResult.Value.Id,
                ocrConfidenceLevel = input.OcrConfidenceLevel,
                ocrLength = input.OcrExtractedJson?.Length ?? 0,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditScannedCopyAttached,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Cnas.Ps.Core.Domain.LaborBooklet),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<LaborBookletDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<LaborBookletDto>> VerifyAsync(
        long bookletId,
        string? notes,
        CancellationToken ct = default)
    {
        var input = new LaborBookletVerifyInputDto(notes);
        var validation = await _verifyValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<LaborBookletDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var entity = await _db.LaborBooklets
            .SingleOrDefaultAsync(b => b.Id == bookletId && b.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<LaborBookletDto>.Failure(ErrorCodes.NotFound, "Labor booklet not found.");
        }
        if (entity.Status != LaborBookletStatus.Pending)
        {
            return Result<LaborBookletDto>.Failure(
                ErrorCodes.Conflict,
                "Only Pending booklets can be verified.");
        }

        var now = _clock.UtcNow;
        entity.Status = LaborBookletStatus.Verified;
        entity.VerifiedAtUtc = now;
        entity.VerifiedByUserId = _caller.UserId;
        if (notes is not null)
        {
            entity.VerifierNotes = notes;
        }
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                laborBookletSqid = _sqids.Encode(entity.Id),
                insuredPersonSqid = _sqids.Encode(entity.InsuredPersonSolicitantId),
                verifiedByUserId = _caller.UserId,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditVerified,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Cnas.Ps.Core.Domain.LaborBooklet),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.LaborBookletVerified.Add(1);

        return Result<LaborBookletDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<LaborBookletDto>> RejectAsync(
        long bookletId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var input = new LaborBookletRejectInputDto(reason);
        var validation = await _rejectValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<LaborBookletDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var entity = await _db.LaborBooklets
            .SingleOrDefaultAsync(b => b.Id == bookletId && b.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<LaborBookletDto>.Failure(ErrorCodes.NotFound, "Labor booklet not found.");
        }
        if (entity.Status != LaborBookletStatus.Pending)
        {
            return Result<LaborBookletDto>.Failure(
                ErrorCodes.Conflict,
                "Only Pending booklets can be rejected.");
        }

        var now = _clock.UtcNow;
        entity.Status = LaborBookletStatus.Rejected;
        entity.RejectedAtUtc = now;
        entity.RejectionReason = reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                laborBookletSqid = _sqids.Encode(entity.Id),
                insuredPersonSqid = _sqids.Encode(entity.InsuredPersonSolicitantId),
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditRejected,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Cnas.Ps.Core.Domain.LaborBooklet),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.LaborBookletRejected.Add(1);

        return Result<LaborBookletDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result> AddPeriodAsync(
        long bookletId,
        InsuredPersonPre1999PeriodInputDto period,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(period);

        var validation = await _periodValidator.ValidateAsync(period, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var booklet = await _db.LaborBooklets
            .SingleOrDefaultAsync(b => b.Id == bookletId && b.IsActive, ct)
            .ConfigureAwait(false);
        if (booklet is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Labor booklet not found.");
        }
        if (booklet.Status == LaborBookletStatus.Rejected)
        {
            return Result.Failure(ErrorCodes.Conflict, "Cannot add a period to a rejected booklet.");
        }

        var now = _clock.UtcNow;
        var entity = new InsuredPersonPre1999Period
        {
            InsuredPersonSolicitantId = booklet.InsuredPersonSolicitantId,
            LaborBookletId = booklet.Id,
            PeriodStartDate = period.PeriodStartDate,
            PeriodEndDate = period.PeriodEndDate,
            EmployerName = period.EmployerName,
            Position = period.Position,
            DaysWorked = period.DaysWorked,
            ProofDocumentReference = period.ProofDocumentReference,
            Notes = period.Notes,
            ValidFromUtc = now,
            ValidToUtc = null,
            ChangeReason = period.ChangeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.InsuredPersonPre1999Periods.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                periodSqid = _sqids.Encode(entity.Id),
                laborBookletSqid = _sqids.Encode(booklet.Id),
                insuredPersonSqid = _sqids.Encode(booklet.InsuredPersonSolicitantId),
                periodStartDate = entity.PeriodStartDate.ToString("O", CultureInfo.InvariantCulture),
                periodEndDate = entity.PeriodEndDate.ToString("O", CultureInfo.InvariantCulture),
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditPeriodAdded,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(InsuredPersonPre1999Period),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.Pre1999PeriodAdded.Add(1);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> AmendPeriodAsync(
        long periodId,
        InsuredPersonPre1999PeriodInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _periodValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var previous = await _db.InsuredPersonPre1999Periods
            .SingleOrDefaultAsync(p => p.Id == periodId && p.IsActive, ct)
            .ConfigureAwait(false);
        if (previous is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Pre-1999 period not found.");
        }
        if (previous.ValidToUtc is not null)
        {
            return Result.Failure(ErrorCodes.Conflict, "Cannot amend a closed period — register a fresh one instead.");
        }

        var now = _clock.UtcNow;
        previous.ValidToUtc = now;
        previous.UpdatedAtUtc = now;
        previous.UpdatedBy = _caller.UserSqid;

        var fresh = new InsuredPersonPre1999Period
        {
            InsuredPersonSolicitantId = previous.InsuredPersonSolicitantId,
            LaborBookletId = previous.LaborBookletId,
            PeriodStartDate = input.PeriodStartDate,
            PeriodEndDate = input.PeriodEndDate,
            EmployerName = input.EmployerName,
            Position = input.Position,
            DaysWorked = input.DaysWorked,
            ProofDocumentReference = input.ProofDocumentReference,
            Notes = input.Notes,
            ValidFromUtc = now,
            ValidToUtc = null,
            ChangeReason = input.ChangeReason,
            RecordedByUserSqid = _caller.UserSqid,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.InsuredPersonPre1999Periods.Add(fresh);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                previousPeriodSqid = _sqids.Encode(previous.Id),
                freshPeriodSqid = _sqids.Encode(fresh.Id),
                insuredPersonSqid = _sqids.Encode(previous.InsuredPersonSolicitantId),
                changeReason = input.ChangeReason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditPeriodAmended,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(InsuredPersonPre1999Period),
            fresh.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.Pre1999PeriodAmended.Add(1);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ClosePeriodAsync(
        long periodId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        // Reuse the reject-input validator for the 3..500-char rule on `reason`.
        var validation = await _rejectValidator.ValidateAsync(
            new LaborBookletRejectInputDto(reason), ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var entity = await _db.InsuredPersonPre1999Periods
            .SingleOrDefaultAsync(p => p.Id == periodId && p.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Pre-1999 period not found.");
        }
        if (entity.ValidToUtc is not null)
        {
            return Result.Failure(ErrorCodes.Conflict, "Pre-1999 period is already closed.");
        }

        var now = _clock.UtcNow;
        entity.ValidToUtc = now;
        entity.ChangeReason = reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                periodSqid = _sqids.Encode(entity.Id),
                insuredPersonSqid = _sqids.Encode(entity.InsuredPersonSolicitantId),
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditPeriodClosed,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(InsuredPersonPre1999Period),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.Pre1999PeriodClosed.Add(1);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<LaborBookletDto?> GetAsync(long bookletId, CancellationToken ct = default)
    {
        var entity = await _db.LaborBooklets
            .SingleOrDefaultAsync(b => b.Id == bookletId && b.IsActive, ct)
            .ConfigureAwait(false);
        return entity is null ? null : ToDto(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InsuredPersonPre1999PeriodDto>> ListPeriodsForInsuredPersonAsync(
        long insuredPersonId,
        CancellationToken ct = default)
    {
        var rows = await _db.InsuredPersonPre1999Periods
            .Where(p => p.InsuredPersonSolicitantId == insuredPersonId && p.IsActive && p.ValidToUtc == null)
            .OrderBy(p => p.PeriodStartDate)
            .ThenBy(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var list = new List<InsuredPersonPre1999PeriodDto>(rows.Count);
        foreach (var row in rows)
        {
            list.Add(ToDto(row));
        }
        return list;
    }

    /// <summary>Projects a <see cref="Cnas.Ps.Core.Domain.LaborBooklet"/> entity to its outbound DTO.</summary>
    /// <param name="entity">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private LaborBookletDto ToDto(Cnas.Ps.Core.Domain.LaborBooklet entity) => new(
        Id: _sqids.Encode(entity.Id),
        InsuredPersonSqid: _sqids.Encode(entity.InsuredPersonSolicitantId),
        CarnetMuncaNumber: entity.CarnetMuncaNumber,
        IssuedDate: entity.IssuedDate,
        IssuingAuthority: entity.IssuingAuthority,
        Status: entity.Status.ToString(),
        OcrConfidenceLevel: entity.OcrConfidenceLevel,
        VerifierNotes: entity.VerifierNotes,
        VerifiedByUserSqid: entity.VerifiedByUserId.HasValue ? _sqids.Encode(entity.VerifiedByUserId.Value) : null,
        VerifiedAtUtc: entity.VerifiedAtUtc,
        RejectionReason: entity.RejectionReason,
        RejectedAtUtc: entity.RejectedAtUtc,
        HasScannedCopy: entity.HasScannedCopy);

    /// <summary>Projects an <see cref="InsuredPersonPre1999Period"/> entity to its outbound DTO.</summary>
    /// <param name="entity">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private InsuredPersonPre1999PeriodDto ToDto(InsuredPersonPre1999Period entity) => new(
        Id: _sqids.Encode(entity.Id),
        InsuredPersonSqid: _sqids.Encode(entity.InsuredPersonSolicitantId),
        LaborBookletSqid: entity.LaborBookletId.HasValue ? _sqids.Encode(entity.LaborBookletId.Value) : null,
        PeriodStartDate: entity.PeriodStartDate,
        PeriodEndDate: entity.PeriodEndDate,
        EmployerName: entity.EmployerName,
        Position: entity.Position,
        DaysWorked: entity.DaysWorked,
        ProofDocumentReference: entity.ProofDocumentReference,
        Notes: entity.Notes,
        ValidFromUtc: entity.ValidFromUtc,
        ValidToUtc: entity.ValidToUtc,
        ChangeReason: entity.ChangeReason);
}
