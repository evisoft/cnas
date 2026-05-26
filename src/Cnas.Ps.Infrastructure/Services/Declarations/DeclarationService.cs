using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Attachments;
using Cnas.Ps.Application.Declarations;
using Cnas.Ps.Application.ManagementPeriods;
using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.Declarations;

/// <summary>
/// R0810 / R0811 / R0812 — concrete implementation of <see cref="IDeclarationService"/>.
/// Owns the three declaration-registration paths plus the per-row adjust /
/// cancel lifecycle. All audit attribution flows through
/// <see cref="IAuditService"/>; all timestamps come from
/// <see cref="ICnasTimeProvider"/>.
/// </summary>
public sealed class DeclarationService : IDeclarationService
{
    /// <summary>Stable audit-event-code prefix used by registration paths.</summary>
    public const string AuditRegisteredPrefix = "DECLARATION.REGISTERED";

    /// <summary>Stable audit event code emitted by <see cref="AdjustAsync"/>.</summary>
    public const string AuditAdjusted = "DECLARATION.ADJUSTED";

    /// <summary>Stable audit event code emitted by <see cref="CancelAsync"/>.</summary>
    public const string AuditCancelled = "DECLARATION.CANCELLED";

    /// <summary>
    /// R0821 — stable audit event code emitted by
    /// <see cref="AttachScannedCopyAsync"/> once the upload succeeds and the
    /// row's <see cref="Declaration.HasScannedCopy"/> flag flips.
    /// </summary>
    public const string AuditScannedCopyAttached = "DECLARATION.SCANNED_COPY_ATTACHED";

    /// <summary>
    /// Stable failure message attached to the <see cref="ErrorCodes.Conflict"/>
    /// result when the natural-key index would reject the insert. Kept as a
    /// constant so callers / tests can branch on it without string matching.
    /// </summary>
    public const string DuplicateMessage = "DECLARATION_DUPLICATE";

    /// <summary>
    /// R0820 — stable failure message attached to the
    /// <see cref="ErrorCodes.ValidationFailed"/> result when the declaration's
    /// reporting month has been closed via
    /// <c>IManagementPeriodService.CloseAsync</c>. Kept as a constant so callers /
    /// tests can branch on it without string matching.
    /// </summary>
    public const string MonthClosedMessage = "MONTH_CLOSED";

    /// <summary>
    /// Cached JSON serializer options — case-insensitive on deserialise,
    /// default on serialise. Reused across audit-payload builders to satisfy
    /// the CA1869 analyzer guidance.
    /// </summary>
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
    private readonly IValidator<DeclarationFromSfsInputDto> _sfsValidator;
    private readonly IValidator<DeclarationAtCnasInputDto> _cnasValidator;
    private readonly IValidator<DeclarationFromOtherDocumentInputDto> _otherValidator;
    private readonly IValidator<DeclarationAdjustInputDto> _adjustValidator;
    private readonly IValidator<DeclarationCancelInputDto> _cancelValidator;
    private readonly IValidator<ScannedDeclarationAttachmentInputDto> _scannedValidator;
    private readonly IValidator<DeclarationsSearchInput> _searchValidator;
    private readonly IAttachmentService _attachments;
    private readonly IQbeToLinqConverter _qbeConverter;
    private readonly IQueryBudgetService _budget;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated caller information for audit attribution.</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="periods">R0820 management-period service consulted before every registration.</param>
    /// <param name="sfsValidator">Validator for the SFS-feed input shape (R0810).</param>
    /// <param name="cnasValidator">Validator for the CNAS-desk input shape (R0811).</param>
    /// <param name="otherValidator">Validator for the supporting-document input shape (R0812).</param>
    /// <param name="adjustValidator">Validator for the adjust-input shape.</param>
    /// <param name="cancelValidator">Validator for the cancel-input shape.</param>
    /// <param name="scannedValidator">R0821 — validator for the scanned-copy upload envelope.</param>
    /// <param name="searchValidator">R0822 — validator for the paged-search input envelope.</param>
    /// <param name="attachments">
    /// R0821 — attachment service used by
    /// <see cref="AttachScannedCopyAsync"/> to persist the scanned PDF / image.
    /// </param>
    /// <param name="qbeConverter">
    /// R0822 — QBE-to-LINQ converter used by <see cref="SearchAsync"/> to
    /// splice the caller's predicate.
    /// </param>
    /// <param name="budget">
    /// R0822 / R0167 — query-budget guard consulted by
    /// <see cref="SearchAsync"/> before materialisation.
    /// </param>
    public DeclarationService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IManagementPeriodService periods,
        IValidator<DeclarationFromSfsInputDto> sfsValidator,
        IValidator<DeclarationAtCnasInputDto> cnasValidator,
        IValidator<DeclarationFromOtherDocumentInputDto> otherValidator,
        IValidator<DeclarationAdjustInputDto> adjustValidator,
        IValidator<DeclarationCancelInputDto> cancelValidator,
        IValidator<ScannedDeclarationAttachmentInputDto> scannedValidator,
        IValidator<DeclarationsSearchInput> searchValidator,
        IAttachmentService attachments,
        IQbeToLinqConverter qbeConverter,
        IQueryBudgetService budget)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentNullException.ThrowIfNull(sfsValidator);
        ArgumentNullException.ThrowIfNull(cnasValidator);
        ArgumentNullException.ThrowIfNull(otherValidator);
        ArgumentNullException.ThrowIfNull(adjustValidator);
        ArgumentNullException.ThrowIfNull(cancelValidator);
        ArgumentNullException.ThrowIfNull(scannedValidator);
        ArgumentNullException.ThrowIfNull(searchValidator);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(qbeConverter);
        ArgumentNullException.ThrowIfNull(budget);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _periods = periods;
        _sfsValidator = sfsValidator;
        _cnasValidator = cnasValidator;
        _otherValidator = otherValidator;
        _adjustValidator = adjustValidator;
        _cancelValidator = cancelValidator;
        _scannedValidator = scannedValidator;
        _searchValidator = searchValidator;
        _attachments = attachments;
        _qbeConverter = qbeConverter;
        _budget = budget;
    }

    /// <summary>
    /// R0820 / BP 1.2-K — consults
    /// <see cref="IManagementPeriodService.IsMonthClosedAsync"/> as a
    /// service-layer guard. Returns <c>true</c> when registration must be
    /// refused (the month is closed and not re-opened).
    /// </summary>
    /// <param name="reportingMonth">Reporting month of the candidate declaration.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns><c>true</c> when the month is closed and registration must be refused.</returns>
    private Task<bool> IsRegistrationRefusedForClosedMonthAsync(
        DateOnly reportingMonth,
        CancellationToken ct)
        => _periods.IsMonthClosedAsync(reportingMonth, ct);

    /// <inheritdoc />
    public async Task<Result<DeclarationDto>> RegisterFromSfsAsync(
        DeclarationFromSfsInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _sfsValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<DeclarationDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.ContributorSqid);
        if (decoded.IsFailure)
        {
            return Result<DeclarationDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var contributorId = decoded.Value;

        // Defensive payer-existence check — the FK has no navigation, so a
        // bogus ContributorId would persist a dangling row otherwise.
        var contributorExists = await _db.Contributors
            .AnyAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (!contributorExists)
        {
            return Result<DeclarationDto>.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        // R0810 — duplicate-key check: SFS rows always carry a reference number,
        // so the (ContributorId, Kind=Sfs, ReportingMonth, ReferenceNumber)
        // tuple identifies an already-ingested row. We surface an explicit
        // failure rather than relying on the database constraint so the test
        // harness (which uses InMemory) sees a deterministic outcome.
        var duplicate = await _db.Declarations.AnyAsync(d =>
            d.ContributorId == contributorId &&
            d.Kind == DeclarationKind.Sfs &&
            d.ReportingMonth == input.ReportingMonth &&
            d.ReferenceNumber == input.ReferenceNumber &&
            d.IsActive,
            ct).ConfigureAwait(false);
        if (duplicate)
        {
            return Result<DeclarationDto>.Failure(ErrorCodes.Conflict, DuplicateMessage);
        }

        if (await IsRegistrationRefusedForClosedMonthAsync(input.ReportingMonth, ct).ConfigureAwait(false))
        {
            return Result<DeclarationDto>.Failure(ErrorCodes.ValidationFailed, MonthClosedMessage);
        }

        return await InsertAndAuditAsync(
            contributorId,
            DeclarationKind.Sfs,
            input.ReportingMonth,
            input.ReferenceNumber,
            input.DeclaredContributionAmount,
            input.Notes,
            input.FiledAtUtc,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<DeclarationDto>> RegisterAtCnasAsync(
        DeclarationAtCnasInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _cnasValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<DeclarationDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.ContributorSqid);
        if (decoded.IsFailure)
        {
            return Result<DeclarationDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var contributorId = decoded.Value;

        if (!Enum.TryParse<DeclarationKind>(input.Kind, ignoreCase: false, out var kind))
        {
            // The validator already rejects this; the guard is defensive.
            return Result<DeclarationDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Kind must be one of BassFour, Bass, BassAn, Pre2018.");
        }

        var contributorExists = await _db.Contributors
            .AnyAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (!contributorExists)
        {
            return Result<DeclarationDto>.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        if (input.ReferenceNumber is not null)
        {
            var duplicate = await _db.Declarations.AnyAsync(d =>
                d.ContributorId == contributorId &&
                d.Kind == kind &&
                d.ReportingMonth == input.ReportingMonth &&
                d.ReferenceNumber == input.ReferenceNumber &&
                d.IsActive,
                ct).ConfigureAwait(false);
            if (duplicate)
            {
                return Result<DeclarationDto>.Failure(ErrorCodes.Conflict, DuplicateMessage);
            }
        }

        if (await IsRegistrationRefusedForClosedMonthAsync(input.ReportingMonth, ct).ConfigureAwait(false))
        {
            return Result<DeclarationDto>.Failure(ErrorCodes.ValidationFailed, MonthClosedMessage);
        }

        return await InsertAndAuditAsync(
            contributorId,
            kind,
            input.ReportingMonth,
            input.ReferenceNumber,
            input.DeclaredContributionAmount,
            input.Notes,
            input.FiledAtUtc,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<DeclarationDto>> RegisterFromOtherDocumentAsync(
        DeclarationFromOtherDocumentInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _otherValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<DeclarationDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(input.ContributorSqid);
        if (decoded.IsFailure)
        {
            return Result<DeclarationDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var contributorId = decoded.Value;

        if (!Enum.TryParse<DeclarationKind>(input.Kind, ignoreCase: false, out var kind))
        {
            return Result<DeclarationDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Kind must be one of Control, CourtDecision, Other.");
        }

        var contributorExists = await _db.Contributors
            .AnyAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (!contributorExists)
        {
            return Result<DeclarationDto>.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        if (input.ReferenceNumber is not null)
        {
            var duplicate = await _db.Declarations.AnyAsync(d =>
                d.ContributorId == contributorId &&
                d.Kind == kind &&
                d.ReportingMonth == input.ReportingMonth &&
                d.ReferenceNumber == input.ReferenceNumber &&
                d.IsActive,
                ct).ConfigureAwait(false);
            if (duplicate)
            {
                return Result<DeclarationDto>.Failure(ErrorCodes.Conflict, DuplicateMessage);
            }
        }

        if (await IsRegistrationRefusedForClosedMonthAsync(input.ReportingMonth, ct).ConfigureAwait(false))
        {
            return Result<DeclarationDto>.Failure(ErrorCodes.ValidationFailed, MonthClosedMessage);
        }

        return await InsertAndAuditAsync(
            contributorId,
            kind,
            input.ReportingMonth,
            input.ReferenceNumber,
            input.DeclaredContributionAmount,
            input.Notes,
            input.FiledAtUtc,
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<DeclarationDto>> AdjustAsync(
        long declarationId,
        decimal adjustedAmount,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        // Reuse the validator so the controller and the service share the same contract.
        var input = new DeclarationAdjustInputDto(adjustedAmount, reason);
        var validation = await _adjustValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<DeclarationDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var entity = await _db.Declarations
            .SingleOrDefaultAsync(d => d.Id == declarationId && d.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<DeclarationDto>.Failure(ErrorCodes.NotFound, "Declaration not found.");
        }
        if (entity.Status == DeclarationStatus.Cancelled)
        {
            return Result<DeclarationDto>.Failure(
                ErrorCodes.Conflict,
                "Cannot adjust a cancelled declaration.");
        }

        var now = _clock.UtcNow;
        entity.AdjustedContributionAmount = adjustedAmount;
        entity.Status = DeclarationStatus.Adjusted;
        entity.Notes = reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                declarationSqid = _sqids.Encode(entity.Id),
                contributorSqid = _sqids.Encode(entity.ContributorId),
                kind = entity.Kind.ToString(),
                reportingMonth = entity.ReportingMonth.ToString("O", CultureInfo.InvariantCulture),
                adjustedAmount,
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditAdjusted,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Declaration),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<DeclarationDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result> CancelAsync(
        long declarationId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var input = new DeclarationCancelInputDto(reason);
        var validation = await _cancelValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var entity = await _db.Declarations
            .SingleOrDefaultAsync(d => d.Id == declarationId && d.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Declaration not found.");
        }
        if (entity.Status == DeclarationStatus.Cancelled)
        {
            return Result.Failure(ErrorCodes.Conflict, "Declaration is already cancelled.");
        }

        var now = _clock.UtcNow;
        entity.Status = DeclarationStatus.Cancelled;
        entity.Notes = reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                declarationSqid = _sqids.Encode(entity.Id),
                contributorSqid = _sqids.Encode(entity.ContributorId),
                kind = entity.Kind.ToString(),
                reportingMonth = entity.ReportingMonth.ToString("O", CultureInfo.InvariantCulture),
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCancelled,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Declaration),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeclarationDto>> ListForPayerAsync(
        long contributorId,
        DateOnly fromMonth,
        DateOnly toMonth,
        CancellationToken ct = default)
    {
        var rows = await _db.Declarations
            .Where(d =>
                d.ContributorId == contributorId &&
                d.IsActive &&
                d.ReportingMonth >= fromMonth &&
                d.ReportingMonth <= toMonth)
            .OrderByDescending(d => d.ReportingMonth)
            .ThenBy(d => d.Kind)
            .ThenBy(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var list = new List<DeclarationDto>(rows.Count);
        foreach (var row in rows)
        {
            list.Add(ToDto(row));
        }
        return list;
    }

    /// <summary>
    /// Inserts a declaration with the supplied attributes, persists it, writes
    /// the Notice audit row, increments the per-kind counter, and projects the
    /// outbound DTO.
    /// </summary>
    /// <param name="contributorId">Raw bigint id of the payer.</param>
    /// <param name="kind">Resolved <see cref="DeclarationKind"/>.</param>
    /// <param name="reportingMonth">Calendar month (Day = 1).</param>
    /// <param name="reference">Optional external reference number.</param>
    /// <param name="declaredAmount">Gross amount declared (MDL).</param>
    /// <param name="notes">Optional operator notes.</param>
    /// <param name="filedAtUtc">Optional override of the filing instant; defaults to <see cref="ICnasTimeProvider.UtcNow"/>.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Success-wrapped <see cref="DeclarationDto"/>.</returns>
    private async Task<Result<DeclarationDto>> InsertAndAuditAsync(
        long contributorId,
        DeclarationKind kind,
        DateOnly reportingMonth,
        string? reference,
        decimal declaredAmount,
        string? notes,
        DateTime? filedAtUtc,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var entity = new Declaration
        {
            ContributorId = contributorId,
            Kind = kind,
            ReportingMonth = reportingMonth,
            FiledAtUtc = filedAtUtc ?? now,
            ReferenceNumber = reference,
            DeclaredContributionAmount = declaredAmount,
            AdjustedContributionAmount = null,
            Status = DeclarationStatus.Received,
            Notes = notes,
            IsArchived = false,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.Declarations.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var kindName = kind.ToString();
        var details = JsonSerializer.Serialize(
            new
            {
                declarationSqid = _sqids.Encode(entity.Id),
                contributorSqid = _sqids.Encode(contributorId),
                kind = kindName,
                reportingMonth = reportingMonth.ToString("O", CultureInfo.InvariantCulture),
                referenceNumber = reference,
                declaredContributionAmount = declaredAmount,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            $"{AuditRegisteredPrefix}.{kindName}",
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Declaration),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.DeclarationRegistered.Add(1, new KeyValuePair<string, object?>("kind", kindName));

        return Result<DeclarationDto>.Success(ToDto(entity));
    }

    /// <summary>Projects a <see cref="Declaration"/> entity into its outbound DTO with Sqid ids.</summary>
    /// <param name="entity">Loaded entity.</param>
    /// <returns>Populated DTO.</returns>
    private DeclarationDto ToDto(Declaration entity) => new(
        Id: _sqids.Encode(entity.Id),
        ContributorSqid: _sqids.Encode(entity.ContributorId),
        Kind: entity.Kind.ToString(),
        ReportingMonth: entity.ReportingMonth,
        FiledAtUtc: entity.FiledAtUtc,
        ReferenceNumber: entity.ReferenceNumber,
        DeclaredContributionAmount: entity.DeclaredContributionAmount,
        AdjustedContributionAmount: entity.AdjustedContributionAmount,
        Status: entity.Status.ToString(),
        Notes: entity.Notes,
        IsArchived: entity.IsArchived,
        HasScannedCopy: entity.HasScannedCopy,
        OcrConfidenceLevel: entity.OcrConfidenceLevel,
        RegisteredByOffice: entity.RegisteredByOffice,
        FormVersion: entity.FormVersion);

    /// <inheritdoc />
    public async Task<Result<DeclarationDto>> AttachScannedCopyAsync(
        long declarationId,
        ScannedDeclarationAttachmentInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _scannedValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<DeclarationDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var entity = await _db.Declarations
            .SingleOrDefaultAsync(d => d.Id == declarationId && d.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<DeclarationDto>.Failure(ErrorCodes.NotFound, "Declaration not found.");
        }
        if (entity.Status == DeclarationStatus.Cancelled)
        {
            return Result<DeclarationDto>.Failure(
                ErrorCodes.Conflict,
                "Cannot attach a scanned copy to a cancelled declaration.");
        }

        // Persist the binary via the R0227 attachment surface — the attachment
        // service performs the magic-byte sniff, size check, sha256 dedup, and
        // emits its own Sensitive audit row. We supply the canonical owner
        // string + Confidential sensitivity so a citizen's scanned form
        // inherits the right access controls.
        var uploadInput = new AttachmentUploadDto(
            OwnerEntityType: AttachmentOwnerTypes.Declaration,
            OwnerSqid: _sqids.Encode(entity.Id),
            ContentBase64: input.FileBase64,
            DeclaredFileName: input.FileName,
            Category: nameof(AttachmentCategory.LegalDocument),
            SensitivityLabel: nameof(Cnas.Ps.Contracts.Security.SensitivityLabel.Confidential),
            Description: null);
        var uploadResult = await _attachments.UploadAsync(uploadInput, ct).ConfigureAwait(false);
        if (uploadResult.IsFailure)
        {
            return Result<DeclarationDto>.Failure(uploadResult.ErrorCode!, uploadResult.ErrorMessage!);
        }

        // Flip the row-level convenience flag + persist any OCR metadata the
        // caller supplied. The optimistic-concurrency token (xmin) is included
        // in the SaveChangesAsync round-trip via EF's standard plumbing.
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
                declarationSqid = _sqids.Encode(entity.Id),
                attachmentSqid = uploadResult.Value.Id,
                contributorSqid = _sqids.Encode(entity.ContributorId),
                kind = entity.Kind.ToString(),
                reportingMonth = entity.ReportingMonth.ToString("O", CultureInfo.InvariantCulture),
                ocrConfidenceLevel = input.OcrConfidenceLevel,
                ocrLength = input.OcrExtractedJson?.Length ?? 0,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            AuditScannedCopyAttached,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Declaration),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.DeclarationScannedAttached.Add(1);

        return Result<DeclarationDto>.Success(ToDto(entity));
    }

    /// <inheritdoc />
    public async Task<Result<DeclarationsListPageDto>> SearchAsync(
        DeclarationsSearchInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _searchValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<DeclarationsListPageDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        // Build the filter-applied queryable BEFORE consulting the budget guard.
        // Only NON-DEFAULT filter values are added to the QueryFilterContext —
        // the budget policy's hint rules inspect that context to decide what
        // refinement nudges to fire.
        IQueryable<Declaration> query = _db.Declarations.Where(d => d.IsActive);
        var ctxBuilder = new QueryFilterContext();

        if (input.Filter is { Conditions.Count: > 0 } wireFilter)
        {
            // Translate the wire-format DTO to the server-side QBE envelope
            // (the converter binds against the server-side enum).
            var domainFilter = ToDomainFilter(wireFilter);
            var predicate = _qbeConverter.Convert<Declaration>(
                QueryBudgetRegistries.Declaration, domainFilter);
            if (predicate.IsFailure)
            {
                return Result<DeclarationsListPageDto>.Failure(
                    predicate.ErrorCode!, predicate.ErrorMessage!);
            }
            query = query.Where(predicate.Value);
            ctxBuilder = ctxBuilder.With(
                "Qbe",
                wireFilter.Conditions.Count.ToString(CultureInfo.InvariantCulture));
            // Mirror the canonical narrowing fields into the context so the
            // budget hint rules know whether the caller pinned a kind / payer
            // through QBE even though the DTO has no first-class slot for
            // either.
            foreach (var condition in wireFilter.Conditions)
            {
                if (string.Equals(condition.FieldName, "Kind", StringComparison.Ordinal))
                {
                    ctxBuilder = ctxBuilder.With("Kind", condition.Value);
                }
                else if (string.Equals(condition.FieldName, "ContributorId", StringComparison.Ordinal))
                {
                    ctxBuilder = ctxBuilder.With("ContributorId", condition.Value);
                }
            }
        }

        if (input.FromUtc is { } from)
        {
            ctxBuilder = ctxBuilder.With("FromUtc", from);
            query = query.Where(d => d.FiledAtUtc >= from);
        }
        if (input.ToUtc is { } to)
        {
            ctxBuilder = ctxBuilder.With("ToUtc", to);
            query = query.Where(d => d.FiledAtUtc < to);
        }

        var verdict = await _budget.EvaluateAsync(
            QueryBudgetRegistries.Declaration,
            query,
            ctxBuilder,
            ct).ConfigureAwait(false);
        if (!verdict.Allowed)
        {
            return Result<DeclarationsListPageDto>.Failure(
                ErrorCodes.QueryTooBroad,
                QueryBudgetFailureEnvelope.FailureMessage);
        }

        var take = Math.Clamp(input.Take, 1, DeclarationsSearchInputValidator.MaxTake);
        var skip = Math.Max(0, input.Skip);

        var rows = await query
            .OrderByDescending(d => d.FiledAtUtc)
            .ThenByDescending(d => d.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = new List<DeclarationDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(ToDto(row));
        }

        return Result<DeclarationsListPageDto>.Success(new DeclarationsListPageDto(
            Items: items,
            TotalCount: verdict.EstimatedRowCount,
            AppliedSuggestions: Array.Empty<string>()));
    }

    /// <summary>
    /// Translates the wire-side <see cref="QbeFilterDto"/> envelope into the
    /// server-side <see cref="QbeFilter"/> shape understood by
    /// <see cref="IQbeToLinqConverter"/>. Unknown operator literals are
    /// translated to a sentinel <see cref="QbeOperator"/> value so the
    /// converter surfaces a stable error code rather than throwing.
    /// </summary>
    /// <param name="dto">Wire-format envelope.</param>
    /// <returns>The corresponding server-side filter.</returns>
    private static QbeFilter ToDomainFilter(QbeFilterDto dto)
    {
        var conditions = new List<QbeCondition>(dto.Conditions.Count);
        foreach (var c in dto.Conditions)
        {
            if (!Enum.TryParse<QbeOperator>(c.Operator, ignoreCase: false, out var op))
            {
                op = (QbeOperator)int.MinValue;
            }
            conditions.Add(new QbeCondition(c.FieldName, op, c.Value, c.Value2));
        }
        return new QbeFilter(
            string.IsNullOrEmpty(dto.Combinator) ? QbeFilter.CombinatorAnd : dto.Combinator,
            conditions);
    }
}
