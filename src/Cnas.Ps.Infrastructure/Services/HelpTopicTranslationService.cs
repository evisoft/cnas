using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Help;
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
/// Default <see cref="IHelpTopicTranslationService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Idempotent (topic, language) upsert; the approve
/// path emits a Critical <c>HELP.APPROVED</c> audit row and triggers a synchronous
/// resolver invalidation.
/// </summary>
public sealed class HelpTopicTranslationService : IHelpTopicTranslationService
{
    /// <summary>Stable audit-event prefix.</summary>
    private const string AuditPrefix = "HELP";

    private readonly ICnasDbContext _db;
    private readonly ICallerContext _caller;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly HelpResolver _resolver;
    private readonly IValidator<HelpTopicTranslationUpsertDto> _validator;

    /// <summary>Constructs the service with its DI dependencies.</summary>
    /// <param name="db">Per-request DbContext.</param>
    /// <param name="caller">Per-request caller context.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC time provider.</param>
    /// <param name="audit">Audit service.</param>
    /// <param name="resolver">Singleton help resolver invalidated after every mutation.</param>
    /// <param name="validator">Body validator.</param>
    public HelpTopicTranslationService(
        ICnasDbContext db,
        ICallerContext caller,
        ISqidService sqids,
        ICnasTimeProvider clock,
        IAuditService audit,
        HelpResolver resolver,
        IValidator<HelpTopicTranslationUpsertDto> validator)
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
    public async Task<Result<HelpTopicTranslationDto>> UpsertAsync(
        string topicSqid,
        string language,
        HelpTopicTranslationUpsertDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<HelpTopicTranslationDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        if (!HelpTopicTranslationUpsertDtoValidator.LanguageIsSupported(language))
        {
            return Result<HelpTopicTranslationDto>.Failure(
                ErrorCodes.ValidationFailed,
                $"Unsupported language '{language}'. Must be one of: {string.Join(", ", TranslationLanguages.All)}.");
        }

        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<HelpTopicTranslationDto>.Failure(ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var decoded = _sqids.TryDecode(topicSqid);
        if (decoded.IsFailure)
        {
            return Result<HelpTopicTranslationDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var topicExists = await _db.HelpTopics
            .AnyAsync(t => t.Id == decoded.Value && t.IsActive, ct)
            .ConfigureAwait(false);
        if (!topicExists)
        {
            return Result<HelpTopicTranslationDto>.Failure(ErrorCodes.NotFound, "Help topic not found.");
        }

        var now = _clock.UtcNow;
        var existing = await _db.HelpTopicTranslations
            .SingleOrDefaultAsync(
                t => t.HelpTopicId == decoded.Value && t.Language == language,
                ct)
            .ConfigureAwait(false);

        HelpTopicTranslation row;
        if (existing is null)
        {
            row = new HelpTopicTranslation
            {
                HelpTopicId = decoded.Value,
                Language = language,
                Title = input.Title,
                BodyMarkdown = input.BodyMarkdown,
                TranslatorNote = string.IsNullOrWhiteSpace(input.TranslatorNote) ? null : input.TranslatorNote,
                IsApproved = false,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.HelpTopicTranslations.Add(row);
        }
        else
        {
            row = existing;
            row.Title = input.Title;
            row.BodyMarkdown = input.BodyMarkdown;
            row.TranslatorNote = string.IsNullOrWhiteSpace(input.TranslatorNote) ? null : input.TranslatorNote;
            row.IsActive = true;
            // Editing drops the row back to draft so a reviewer must re-approve.
            row.IsApproved = false;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result<HelpTopicTranslationDto>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<HelpTopicTranslationDto>> ApproveAsync(
        string translationSqid,
        CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<HelpTopicTranslationDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(translationSqid);
        if (decoded.IsFailure)
        {
            return Result<HelpTopicTranslationDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.HelpTopicTranslations
            .SingleOrDefaultAsync(t => t.Id == decoded.Value && t.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<HelpTopicTranslationDto>.Failure(ErrorCodes.NotFound, "Help translation not found.");
        }

        if (row.IsApproved)
        {
            return Result<HelpTopicTranslationDto>.Success(Project(row));
        }

        var topic = await _db.HelpTopics
            .SingleOrDefaultAsync(t => t.Id == row.HelpTopicId, ct)
            .ConfigureAwait(false);

        row.IsApproved = true;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.APPROVED", row, topic?.Code, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result<HelpTopicTranslationDto>.Success(Project(row));
    }

    /// <summary>Emits a Critical audit row capturing the (code, language) pair.</summary>
    /// <param name="eventCode">Stable audit event code.</param>
    /// <param name="row">The just-modified row.</param>
    /// <param name="topicCode">Parent topic code; null when the topic lookup failed.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(
        string eventCode,
        HelpTopicTranslation row,
        string? topicCode,
        CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            helpTopicId = _sqids.Encode(row.HelpTopicId),
            code = topicCode,
            language = row.Language,
            isApproved = row.IsApproved,
        });

        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Critical,
            actorId: actor,
            targetEntity: nameof(HelpTopicTranslation),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Projects the entity into its wire DTO.</summary>
    /// <param name="row">Loaded entity row.</param>
    /// <returns>The DTO.</returns>
    private HelpTopicTranslationDto Project(HelpTopicTranslation row) => new(
        Id: _sqids.Encode(row.Id),
        Language: row.Language,
        Title: row.Title,
        BodyMarkdown: row.BodyMarkdown,
        IsApproved: row.IsApproved,
        TranslatorNote: row.TranslatorNote);
}
