using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Localization;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="ITranslationValueService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. The natural-key upsert applies the
/// (key, language) invariant idempotently; the approve path emits a Critical
/// <c>TRANSLATION.APPROVED</c> audit row and triggers a synchronous resolver
/// invalidation.
/// </summary>
public sealed class TranslationValueService : ITranslationValueService
{
    /// <summary>Stable audit-event prefix.</summary>
    private const string AuditPrefix = "TRANSLATION";

    private readonly ICnasDbContext _db;
    private readonly ICallerContext _caller;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly TranslationResolver _resolver;
    private readonly IValidator<TranslationValueUpsertDto> _validator;

    /// <summary>Constructs the service with its DI dependencies.</summary>
    /// <param name="db">Per-request DbContext.</param>
    /// <param name="caller">Per-request caller context.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC time provider.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="resolver">Singleton translation resolver invalidated after every mutation.</param>
    /// <param name="validator">Body validator.</param>
    public TranslationValueService(
        ICnasDbContext db,
        ICallerContext caller,
        ISqidService sqids,
        ICnasTimeProvider clock,
        IAuditService audit,
        TranslationResolver resolver,
        IValidator<TranslationValueUpsertDto> validator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(validator);
        _db = db;
        _caller = caller;
        _sqids = sqids;
        _clock = clock;
        _audit = audit;
        _resolver = resolver;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<TranslationValueDto>> UpsertAsync(
        string keySqid,
        string language,
        TranslationValueUpsertDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<TranslationValueDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        if (!TranslationValueUpsertDtoValidator.LanguageIsSupported(language))
        {
            return Result<TranslationValueDto>.Failure(
                ErrorCodes.ValidationFailed,
                $"Unsupported language '{language}'. Must be one of: {string.Join(", ", TranslationLanguages.All)}.");
        }

        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<TranslationValueDto>.Failure(ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var decoded = _sqids.TryDecode(keySqid);
        if (decoded.IsFailure)
        {
            return Result<TranslationValueDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var keyExists = await _db.TranslationKeys
            .AnyAsync(k => k.Id == decoded.Value && k.IsActive, ct)
            .ConfigureAwait(false);
        if (!keyExists)
        {
            return Result<TranslationValueDto>.Failure(ErrorCodes.NotFound, "Translation key not found.");
        }

        var now = _clock.UtcNow;
        var existing = await _db.TranslationValues
            .SingleOrDefaultAsync(
                v => v.TranslationKeyId == decoded.Value && v.Language == language,
                ct)
            .ConfigureAwait(false);

        TranslationValue row;
        if (existing is null)
        {
            row = new TranslationValue
            {
                TranslationKeyId = decoded.Value,
                Language = language,
                Text = input.Text,
                TranslatorNote = string.IsNullOrWhiteSpace(input.TranslatorNote) ? null : input.TranslatorNote,
                IsApproved = false,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.TranslationValues.Add(row);
        }
        else
        {
            row = existing;
            row.Text = input.Text;
            row.TranslatorNote = string.IsNullOrWhiteSpace(input.TranslatorNote) ? null : input.TranslatorNote;
            row.IsActive = true;
            // Editing an approved value drops it back to draft so a reviewer must
            // re-approve. This is the documented review-on-edit invariant.
            row.IsApproved = false;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Refresh the resolver so the new text is visible immediately.
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result<TranslationValueDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<TranslationValueDto>> ApproveAsync(
        string valueSqid,
        CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<TranslationValueDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(valueSqid);
        if (decoded.IsFailure)
        {
            return Result<TranslationValueDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.TranslationValues
            .SingleOrDefaultAsync(v => v.Id == decoded.Value && v.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<TranslationValueDto>.Failure(ErrorCodes.NotFound, "Translation value not found.");
        }

        if (row.IsApproved)
        {
            // Idempotent — no audit row, no resolver invalidation.
            return Result<TranslationValueDto>.Success(Project(row));
        }

        var key = await _db.TranslationKeys
            .SingleOrDefaultAsync(k => k.Id == row.TranslationKeyId, ct)
            .ConfigureAwait(false);

        row.IsApproved = true;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.APPROVED", row, key?.Code, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result<TranslationValueDto>.Success(Project(row));
    }

    /// <summary>
    /// Emits a Critical-severity audit row capturing the (code, language) pair so
    /// reviewers' activity is traceable end-to-end.
    /// </summary>
    /// <param name="eventCode">Stable audit event code (e.g. <c>TRANSLATION.APPROVED</c>).</param>
    /// <param name="row">The just-modified row.</param>
    /// <param name="keyCode">Parent key code; null when the key lookup failed (defensive).</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(
        string eventCode,
        TranslationValue row,
        string? keyCode,
        CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            translationKeyId = _sqids.Encode(row.TranslationKeyId),
            code = keyCode,
            language = row.Language,
            isApproved = row.IsApproved,
        });

        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Critical,
            actorId: actor,
            targetEntity: nameof(TranslationValue),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Projects the entity into its wire DTO.</summary>
    /// <param name="row">Loaded entity row.</param>
    /// <returns>The DTO the API surface returns.</returns>
    private TranslationValueDto Project(TranslationValue row) => new(
        Id: _sqids.Encode(row.Id),
        Language: row.Language,
        Text: row.Text,
        IsApproved: row.IsApproved,
        TranslatorNote: row.TranslatorNote);
}
