using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.ExecutoryDocuments;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.ExecutoryDocuments;

/// <summary>
/// R1600 / R1406 / TOR Annex 3.8 / §3.6-G — concrete implementation of
/// <see cref="IExecutoryDocumentService"/>. Owns the registry lifecycle
/// (register, modify, suspend / resume, cancel, complete) plus the running-
/// tally accumulation that drives the auto-complete transition.
/// </summary>
/// <remarks>
/// <para>
/// <b>DocumentSeriesNumber generation.</b> <see cref="RegisterAsync"/> uses
/// the caller-supplied <c>DocumentSeriesNumber</c> when present; otherwise
/// it auto-generates <c>EXE-{year}-{seq:000000}</c> where the sequence is
/// per-year. A retry loop tolerates the rare contention case where two
/// concurrent registrations collide on the unique index.
/// </para>
/// <para>
/// <b>Audit + metric.</b> Every lifecycle transition emits the canonical
/// stable audit event at <see cref="AuditSeverity.Critical"/> severity per
/// CLAUDE.md §5.6 (PII / financial data). The per-withholding event is
/// emitted at <see cref="AuditSeverity.Information"/> — the per-payment
/// rate would otherwise dominate the audit-stream.
/// </para>
/// <para>
/// <b>PII safety.</b> Audit payloads NEVER contain the plaintext IDNP or
/// IBAN — only the last 4 characters of the IBAN (per the
/// <c>CreditorAccountIbanLast4</c> convention) and the Sqid id of the
/// document. The plaintext columns are encrypted at rest by the
/// <c>EncryptedStringConverter</c> wired on <c>CnasDbContext.OnModelCreating</c>.
/// </para>
/// </remarks>
public sealed class ExecutoryDocumentService : IExecutoryDocumentService
{
    /// <summary>Stable audit event code emitted when a document is registered.</summary>
    public const string AuditRegistered = "EXECUTORY_DOC.REGISTERED";

    /// <summary>Stable audit event code emitted when a document is modified.</summary>
    public const string AuditModified = "EXECUTORY_DOC.MODIFIED";

    /// <summary>Stable audit event code emitted when a document is suspended.</summary>
    public const string AuditSuspended = "EXECUTORY_DOC.SUSPENDED";

    /// <summary>Stable audit event code emitted when a document is resumed.</summary>
    public const string AuditResumed = "EXECUTORY_DOC.RESUMED";

    /// <summary>Stable audit event code emitted when a document is cancelled.</summary>
    public const string AuditCancelled = "EXECUTORY_DOC.CANCELLED";

    /// <summary>Stable audit event code emitted when a document is completed.</summary>
    public const string AuditCompleted = "EXECUTORY_DOC.COMPLETED";

    /// <summary>Stable audit event code emitted on every withholding append.</summary>
    public const string AuditWithheld = "EXECUTORY_DOC.WITHHELD";

    /// <summary>Stable failure message for terminal-state transitions.</summary>
    public const string TerminalStateMessage = "EXECUTORY_DOC_TERMINAL_STATE";

    /// <summary>Stable failure message for unsupported state transitions (e.g. suspend on a cancelled row).</summary>
    public const string InvalidStateTransitionMessage = "EXECUTORY_DOC_INVALID_TRANSITION";

    /// <summary>Stable failure message when no debt total has been recorded but the operator forces completion.</summary>
    public const string CompletePrematureMessage = "EXECUTORY_DOC_NOT_FULLY_WITHHELD";

    /// <summary>Stable failure message when the caller-supplied series number collides with an existing row.</summary>
    public const string SeriesNumberDuplicateMessage = "EXECUTORY_DOC_SERIES_DUPLICATE";

    /// <summary>Maximum re-attempts when the unique series-number index rejects a freshly-generated number under contention.</summary>
    private const int MaxSeriesNumberRetries = 5;

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
    private readonly IDeterministicHasher _hasher;
    private readonly IValidator<ExecutoryDocumentRegisterInputDto> _registerValidator;
    private readonly IValidator<ExecutoryDocumentModifyInputDto> _modifyValidator;
    private readonly IValidator<ExecutoryDocumentReasonInputDto> _reasonValidator;

    /// <summary>Constructs the service with its collaborators.</summary>
    /// <param name="db">EF Core context abstraction (write surface).</param>
    /// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly from service code.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="caller">Authenticated-caller information for audit attribution.</param>
    /// <param name="audit">Audit-journal façade.</param>
    /// <param name="hasher">Deterministic hasher used to maintain the IDNP / IBAN shadow hash columns.</param>
    /// <param name="registerValidator">Validator for register input.</param>
    /// <param name="modifyValidator">Validator for modify input.</param>
    /// <param name="reasonValidator">Validator for suspend / resume / cancel input.</param>
    public ExecutoryDocumentService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ICallerContext caller,
        IAuditService audit,
        IDeterministicHasher hasher,
        IValidator<ExecutoryDocumentRegisterInputDto> registerValidator,
        IValidator<ExecutoryDocumentModifyInputDto> modifyValidator,
        IValidator<ExecutoryDocumentReasonInputDto> reasonValidator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(registerValidator);
        ArgumentNullException.ThrowIfNull(modifyValidator);
        ArgumentNullException.ThrowIfNull(reasonValidator);
        _db = db;
        _clock = clock;
        _sqids = sqids;
        _caller = caller;
        _audit = audit;
        _hasher = hasher;
        _registerValidator = registerValidator;
        _modifyValidator = modifyValidator;
        _reasonValidator = reasonValidator;
    }

    /// <inheritdoc />
    public async Task<Result<ExecutoryDocumentDto>> RegisterAsync(
        ExecutoryDocumentRegisterInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _registerValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ExecutoryDocumentDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        if (!Enum.TryParse<ExecutoryDocumentKind>(input.Kind, ignoreCase: false, out var kind))
        {
            return Result<ExecutoryDocumentDto>.Failure(
                ErrorCodes.ValidationFailed, "Kind must be a known ExecutoryDocumentKind enum name.");
        }
        if (!Enum.TryParse<ExecutoryDocumentWithholdingMode>(input.WithholdingMode, ignoreCase: false, out var mode))
        {
            return Result<ExecutoryDocumentDto>.Failure(
                ErrorCodes.ValidationFailed, "WithholdingMode must be a known ExecutoryDocumentWithholdingMode enum name.");
        }

        // Caller-supplied series number takes precedence; otherwise auto-generate
        // EXE-{year}-{seq:000000}. A retry loop tolerates contention on the
        // unique index.
        var canonicalIdnp = input.DebtorIdnp.Trim().ToUpperInvariant();
        var canonicalIban = input.CreditorAccountIban.Trim().ToUpperInvariant();
        var idnpHash = _hasher.ComputeHash(canonicalIdnp);
        var ibanHash = _hasher.ComputeHash(canonicalIban);
        var now = _clock.UtcNow;

        ExecutoryDocument? created = null;
        DbUpdateException? lastUniquenessFailure = null;
        var explicitSeries = !string.IsNullOrWhiteSpace(input.DocumentSeriesNumber);
        for (var attempt = 0; attempt < MaxSeriesNumberRetries; attempt++)
        {
            string seriesNumber;
            if (explicitSeries)
            {
                seriesNumber = input.DocumentSeriesNumber!;

                // Defensive duplicate check on the caller-supplied series so we
                // surface a friendly conflict rather than wait for the DB to
                // throw on unique-index violation.
                var exists = await _db.ExecutoryDocuments
                    .AnyAsync(d => d.DocumentSeriesNumber == seriesNumber, ct)
                    .ConfigureAwait(false);
                if (exists)
                {
                    return Result<ExecutoryDocumentDto>.Failure(
                        ErrorCodes.Conflict, SeriesNumberDuplicateMessage);
                }
            }
            else
            {
                var year = input.EffectiveFrom.Year;
                var prefix = $"EXE-{year}-";
                var existingCount = await _db.ExecutoryDocuments
                    .CountAsync(d => d.DocumentSeriesNumber.StartsWith(prefix), ct)
                    .ConfigureAwait(false);
                seriesNumber = $"{prefix}{(existingCount + 1):D6}";
            }

            var entity = new ExecutoryDocument
            {
                DocumentSeriesNumber = seriesNumber,
                DebtorIdnp = canonicalIdnp,
                DebtorIdnpHash = idnpHash,
                Kind = kind,
                Status = ExecutoryDocumentStatus.Active,
                IssuedBy = input.IssuedBy,
                IssuedDate = input.IssuedDate,
                EffectiveFrom = input.EffectiveFrom,
                EffectiveUntil = input.EffectiveUntil,
                WithholdingMode = mode,
                WithholdingAmountMdl = input.WithholdingAmountMdl,
                WithholdingPercentage = input.WithholdingPercentage,
                PriorityRank = input.PriorityRank,
                CreditorAccountIban = canonicalIban,
                CreditorAccountIbanHash = ibanHash,
                CreditorName = input.CreditorName,
                TotalOwedMdl = input.TotalOwedMdl,
                TotalWithheldMdl = 0m,
                RegisteredByUserId = (int)(_caller.UserId ?? 0),
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.ExecutoryDocuments.Add(entity);

            try
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                created = entity;
                break;
            }
            catch (DbUpdateException ex)
            {
                lastUniquenessFailure = ex;
                _db.ExecutoryDocuments.Remove(entity);
                // Explicit series → no retry; the duplicate is the caller's fault.
                if (explicitSeries)
                {
                    break;
                }
            }
        }
        if (created is null)
        {
            return Result<ExecutoryDocumentDto>.Failure(
                ErrorCodes.Conflict,
                lastUniquenessFailure?.Message ?? "DocumentSeriesNumber generation contention exceeded retry budget.");
        }

        var details = JsonSerializer.Serialize(new
        {
            documentSqid = _sqids.Encode(created.Id),
            documentSeriesNumber = created.DocumentSeriesNumber,
            kind = created.Kind.ToString(),
            mode = created.WithholdingMode.ToString(),
            priorityRank = created.PriorityRank,
            creditorIbanLast4 = LastFour(canonicalIban),
            // No plaintext IDNP / full IBAN — only Sqid + masked references.
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditRegistered,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(ExecutoryDocument),
            created.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ExecutoryDocumentRegistered.Add(1);

        return Result<ExecutoryDocumentDto>.Success(ToDto(created));
    }

    /// <inheritdoc />
    public async Task<Result<ExecutoryDocumentDto>> ModifyAsync(
        string sqid,
        ExecutoryDocumentModifyInputDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _modifyValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ExecutoryDocumentDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ExecutoryDocumentDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var docId = decoded.Value;

        var doc = await _db.ExecutoryDocuments
            .SingleOrDefaultAsync(d => d.Id == docId && d.IsActive, ct)
            .ConfigureAwait(false);
        if (doc is null)
        {
            return Result<ExecutoryDocumentDto>.Failure(ErrorCodes.NotFound, "Executory document not found.");
        }
        if (doc.Status is ExecutoryDocumentStatus.Completed or ExecutoryDocumentStatus.Cancelled)
        {
            return Result<ExecutoryDocumentDto>.Failure(ErrorCodes.Conflict, TerminalStateMessage);
        }

        // Apply each non-null patch field.
        if (input.IssuedBy is not null)
        {
            doc.IssuedBy = input.IssuedBy;
        }
        if (input.EffectiveUntil.HasValue)
        {
            doc.EffectiveUntil = input.EffectiveUntil.Value;
        }
        if (input.WithholdingMode is not null)
        {
            if (!Enum.TryParse<ExecutoryDocumentWithholdingMode>(input.WithholdingMode, ignoreCase: false, out var newMode))
            {
                return Result<ExecutoryDocumentDto>.Failure(
                    ErrorCodes.ValidationFailed, "WithholdingMode must be a known ExecutoryDocumentWithholdingMode enum name.");
            }
            doc.WithholdingMode = newMode;
        }
        if (input.WithholdingAmountMdl.HasValue)
        {
            doc.WithholdingAmountMdl = input.WithholdingAmountMdl.Value;
        }
        if (input.WithholdingPercentage.HasValue)
        {
            doc.WithholdingPercentage = input.WithholdingPercentage.Value;
        }
        if (input.PriorityRank.HasValue)
        {
            doc.PriorityRank = input.PriorityRank.Value;
        }
        if (input.CreditorAccountIban is not null)
        {
            var canonicalIban = input.CreditorAccountIban.Trim().ToUpperInvariant();
            doc.CreditorAccountIban = canonicalIban;
            doc.CreditorAccountIbanHash = _hasher.ComputeHash(canonicalIban);
        }
        if (input.CreditorName is not null)
        {
            doc.CreditorName = input.CreditorName;
        }
        if (input.TotalOwedMdl.HasValue)
        {
            doc.TotalOwedMdl = input.TotalOwedMdl.Value;
        }

        var now = _clock.UtcNow;
        doc.UpdatedAtUtc = now;
        doc.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            documentSqid = _sqids.Encode(doc.Id),
            documentSeriesNumber = doc.DocumentSeriesNumber,
            changeReason = input.ChangeReason,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditModified,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(ExecutoryDocument),
            doc.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<ExecutoryDocumentDto>.Success(ToDto(doc));
    }

    /// <inheritdoc />
    public Task<Result<ExecutoryDocumentDto>> SuspendAsync(
        string sqid,
        ExecutoryDocumentReasonInputDto input,
        CancellationToken ct = default) =>
        TransitionAsync(
            sqid,
            input,
            requiredCurrent: new[] { ExecutoryDocumentStatus.Active },
            target: ExecutoryDocumentStatus.Suspended,
            auditCode: AuditSuspended,
            applyExtras: null,
            ct);

    /// <inheritdoc />
    public Task<Result<ExecutoryDocumentDto>> ResumeAsync(
        string sqid,
        ExecutoryDocumentReasonInputDto input,
        CancellationToken ct = default) =>
        TransitionAsync(
            sqid,
            input,
            requiredCurrent: new[] { ExecutoryDocumentStatus.Suspended },
            target: ExecutoryDocumentStatus.Active,
            auditCode: AuditResumed,
            applyExtras: null,
            ct);

    /// <inheritdoc />
    public Task<Result<ExecutoryDocumentDto>> CancelAsync(
        string sqid,
        ExecutoryDocumentReasonInputDto input,
        CancellationToken ct = default) =>
        TransitionAsync(
            sqid,
            input,
            requiredCurrent: new[]
            {
                ExecutoryDocumentStatus.Active,
                ExecutoryDocumentStatus.Suspended,
            },
            target: ExecutoryDocumentStatus.Cancelled,
            auditCode: AuditCancelled,
            applyExtras: (doc, reason) =>
            {
                doc.CancellationReason = reason;
            },
            ct);

    /// <inheritdoc />
    public async Task<Result<ExecutoryDocumentDto>> CompleteAsync(string sqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ExecutoryDocumentDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var docId = decoded.Value;

        var doc = await _db.ExecutoryDocuments
            .SingleOrDefaultAsync(d => d.Id == docId && d.IsActive, ct)
            .ConfigureAwait(false);
        if (doc is null)
        {
            return Result<ExecutoryDocumentDto>.Failure(ErrorCodes.NotFound, "Executory document not found.");
        }
        if (doc.Status != ExecutoryDocumentStatus.Active)
        {
            return Result<ExecutoryDocumentDto>.Failure(ErrorCodes.Conflict, InvalidStateTransitionMessage);
        }
        // Operator may force-complete an open-ended document; otherwise the
        // tally must have reached the total owed.
        if (doc.TotalOwedMdl.HasValue && doc.TotalWithheldMdl < doc.TotalOwedMdl.Value)
        {
            return Result<ExecutoryDocumentDto>.Failure(ErrorCodes.Conflict, CompletePrematureMessage);
        }

        var now = _clock.UtcNow;
        doc.Status = ExecutoryDocumentStatus.Completed;
        doc.CompletedDate = DateOnly.FromDateTime(now);
        doc.UpdatedAtUtc = now;
        doc.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            documentSqid = _sqids.Encode(doc.Id),
            documentSeriesNumber = doc.DocumentSeriesNumber,
            totalWithheldMdl = doc.TotalWithheldMdl,
            totalOwedMdl = doc.TotalOwedMdl,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditCompleted,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(ExecutoryDocument),
            doc.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<ExecutoryDocumentDto>.Success(ToDto(doc));
    }

    /// <inheritdoc />
    public async Task<Result<ExecutoryDocumentDto>> RecordWithholdingAsync(
        string sqid,
        decimal amountMdl,
        string sourceReference,
        CancellationToken ct = default)
    {
        if (amountMdl <= 0m)
        {
            return Result<ExecutoryDocumentDto>.Failure(
                ErrorCodes.ValidationFailed, "amountMdl must be > 0.");
        }
        if (string.IsNullOrWhiteSpace(sourceReference))
        {
            return Result<ExecutoryDocumentDto>.Failure(
                ErrorCodes.ValidationFailed, "sourceReference is required.");
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ExecutoryDocumentDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var docId = decoded.Value;

        var doc = await _db.ExecutoryDocuments
            .SingleOrDefaultAsync(d => d.Id == docId && d.IsActive, ct)
            .ConfigureAwait(false);
        if (doc is null)
        {
            return Result<ExecutoryDocumentDto>.Failure(ErrorCodes.NotFound, "Executory document not found.");
        }
        if (doc.Status is ExecutoryDocumentStatus.Completed or ExecutoryDocumentStatus.Cancelled)
        {
            return Result<ExecutoryDocumentDto>.Failure(ErrorCodes.Conflict, TerminalStateMessage);
        }
        if (doc.Status == ExecutoryDocumentStatus.Suspended)
        {
            return Result<ExecutoryDocumentDto>.Failure(
                ErrorCodes.Conflict, InvalidStateTransitionMessage);
        }

        var now = _clock.UtcNow;
        doc.TotalWithheldMdl += amountMdl;

        var autoCompleted = false;
        if (doc.TotalOwedMdl.HasValue && doc.TotalWithheldMdl >= doc.TotalOwedMdl.Value)
        {
            doc.Status = ExecutoryDocumentStatus.Completed;
            doc.CompletedDate = DateOnly.FromDateTime(now);
            autoCompleted = true;
        }
        doc.UpdatedAtUtc = now;
        doc.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var withheldDetails = JsonSerializer.Serialize(new
        {
            documentSqid = _sqids.Encode(doc.Id),
            documentSeriesNumber = doc.DocumentSeriesNumber,
            amountMdl,
            runningTotalMdl = doc.TotalWithheldMdl,
            sourceReference,
            priorityRank = doc.PriorityRank,
            creditorIbanLast4 = LastFour(doc.CreditorAccountIban),
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            AuditWithheld,
            AuditSeverity.Information,
            _caller.UserSqid ?? "?",
            nameof(ExecutoryDocument),
            doc.Id,
            withheldDetails,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        if (autoCompleted)
        {
            var completeDetails = JsonSerializer.Serialize(new
            {
                documentSqid = _sqids.Encode(doc.Id),
                documentSeriesNumber = doc.DocumentSeriesNumber,
                totalWithheldMdl = doc.TotalWithheldMdl,
                totalOwedMdl = doc.TotalOwedMdl,
                auto = true,
            }, CachedJsonOptions);
            await _audit.RecordAsync(
                AuditCompleted,
                AuditSeverity.Critical,
                _caller.UserSqid ?? "?",
                nameof(ExecutoryDocument),
                doc.Id,
                completeDetails,
                _caller.SourceIp,
                _caller.CorrelationId,
                ct).ConfigureAwait(false);
        }

        return Result<ExecutoryDocumentDto>.Success(ToDto(doc));
    }

    /// <inheritdoc />
    public async Task<Result<ExecutoryDocumentDto>> GetByIdAsync(string sqid, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ExecutoryDocumentDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var doc = await _db.ExecutoryDocuments
            .SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, ct)
            .ConfigureAwait(false);
        return doc is null
            ? Result<ExecutoryDocumentDto>.Failure(ErrorCodes.NotFound, "Executory document not found.")
            : Result<ExecutoryDocumentDto>.Success(ToDto(doc));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ExecutoryDocumentDto>>> ListByDebtorAsync(
        string debtorIdnp,
        ExecutoryDocumentStatusFilter? statusFilter,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(debtorIdnp))
        {
            return Result<IReadOnlyList<ExecutoryDocumentDto>>.Failure(
                ErrorCodes.ValidationFailed, "debtorIdnp is required.");
        }

        var canonical = debtorIdnp.Trim().ToUpperInvariant();
        var hash = _hasher.ComputeHash(canonical);

        IQueryable<ExecutoryDocument> query = _db.ExecutoryDocuments
            .Where(d => d.IsActive && d.DebtorIdnpHash == hash);
        if (statusFilter is not null)
        {
            var status = statusFilter.Status;
            query = query.Where(d => d.Status == status);
        }

        var rows = await query
            .OrderBy(d => d.PriorityRank)
            .ThenByDescending(d => d.IssuedDate)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IReadOnlyList<ExecutoryDocumentDto> result = rows.Select(ToDto).ToList();
        return Result<IReadOnlyList<ExecutoryDocumentDto>>.Success(result);
    }

    /// <summary>
    /// Shared lifecycle-transition helper used by Suspend / Resume / Cancel.
    /// Validates the reason payload, flips the status, runs an optional
    /// extras delegate to record cancellation reason etc., and emits the
    /// stable audit event.
    /// </summary>
    /// <param name="sqid">Sqid-encoded document id.</param>
    /// <param name="input">Reason payload.</param>
    /// <param name="requiredCurrent">Statuses from which the transition is allowed.</param>
    /// <param name="target">Status to transition into.</param>
    /// <param name="auditCode">Stable audit event code to emit.</param>
    /// <param name="applyExtras">Optional delegate run before persistence (e.g. to stamp <c>CancellationReason</c>).</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>Updated DTO on success; conflict / not-found / validation-failed otherwise.</returns>
    private async Task<Result<ExecutoryDocumentDto>> TransitionAsync(
        string sqid,
        ExecutoryDocumentReasonInputDto input,
        ExecutoryDocumentStatus[] requiredCurrent,
        ExecutoryDocumentStatus target,
        string auditCode,
        Action<ExecutoryDocument, string>? applyExtras,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = await _reasonValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<ExecutoryDocumentDto>.Failure(
                ErrorCodes.ValidationFailed,
                validation.Errors[0].ErrorMessage);
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<ExecutoryDocumentDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }
        var docId = decoded.Value;

        var doc = await _db.ExecutoryDocuments
            .SingleOrDefaultAsync(d => d.Id == docId && d.IsActive, ct)
            .ConfigureAwait(false);
        if (doc is null)
        {
            return Result<ExecutoryDocumentDto>.Failure(ErrorCodes.NotFound, "Executory document not found.");
        }
        if (!requiredCurrent.Contains(doc.Status))
        {
            return Result<ExecutoryDocumentDto>.Failure(ErrorCodes.Conflict, InvalidStateTransitionMessage);
        }

        var now = _clock.UtcNow;
        doc.Status = target;
        applyExtras?.Invoke(doc, input.Reason);
        doc.UpdatedAtUtc = now;
        doc.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(new
        {
            documentSqid = _sqids.Encode(doc.Id),
            documentSeriesNumber = doc.DocumentSeriesNumber,
            status = doc.Status.ToString(),
            reason = input.Reason,
        }, CachedJsonOptions);
        await _audit.RecordAsync(
            auditCode,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(ExecutoryDocument),
            doc.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<ExecutoryDocumentDto>.Success(ToDto(doc));
    }

    /// <summary>Projects an entity into the wire DTO.</summary>
    /// <param name="doc">Loaded executory-document entity.</param>
    /// <returns>The DTO.</returns>
    private ExecutoryDocumentDto ToDto(ExecutoryDocument doc) => new(
        Id: _sqids.Encode(doc.Id),
        DocumentSeriesNumber: doc.DocumentSeriesNumber,
        DebtorIdnp: doc.DebtorIdnp,
        Kind: doc.Kind.ToString(),
        Status: doc.Status.ToString(),
        IssuedBy: doc.IssuedBy,
        IssuedDate: doc.IssuedDate,
        EffectiveFrom: doc.EffectiveFrom,
        EffectiveUntil: doc.EffectiveUntil,
        WithholdingMode: doc.WithholdingMode.ToString(),
        WithholdingAmountMdl: doc.WithholdingAmountMdl,
        WithholdingPercentage: doc.WithholdingPercentage,
        PriorityRank: doc.PriorityRank,
        CreditorAccountIban: doc.CreditorAccountIban,
        CreditorName: doc.CreditorName,
        TotalOwedMdl: doc.TotalOwedMdl,
        TotalWithheldMdl: doc.TotalWithheldMdl,
        CompletedDate: doc.CompletedDate,
        CancellationReason: doc.CancellationReason);

    /// <summary>Returns the last 4 characters of <paramref name="value"/> (or fewer when the string is shorter).</summary>
    /// <param name="value">String to mask.</param>
    /// <returns>The masked tail; never null.</returns>
    private static string LastFour(string value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= 4 ? value : value[^4..];
}
