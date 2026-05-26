using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Help;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IHelpTopicService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Every mutation triggers a synchronous
/// <see cref="HelpResolver"/> invalidation so the new shape is visible to the next
/// help-widget call without waiting for the 60 s background refresh.
/// </summary>
public sealed class HelpTopicService : IHelpTopicService
{
    private readonly ICnasDbContext _db;
    private readonly ICallerContext _caller;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly HelpResolver _resolver;
    private readonly IValidator<HelpTopicUpsertDto> _validator;

    /// <summary>Constructs the service with its DI dependencies.</summary>
    /// <param name="db">Per-request DbContext.</param>
    /// <param name="caller">Per-request caller context.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC time provider.</param>
    /// <param name="resolver">Singleton help resolver invalidated after every mutation.</param>
    /// <param name="validator">Body validator.</param>
    public HelpTopicService(
        ICnasDbContext db,
        ICallerContext caller,
        ISqidService sqids,
        ICnasTimeProvider clock,
        HelpResolver resolver,
        IValidator<HelpTopicUpsertDto> validator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(validator);
        _db = db;
        _caller = caller;
        _sqids = sqids;
        _clock = clock;
        _resolver = resolver;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<HelpTopicDto>>> ListAsync(
        string? module,
        CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<IReadOnlyList<HelpTopicDto>>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var topics = await _db.HelpTopics
            .Where(t => t.IsActive)
            .Where(t => module == null || t.Module == module)
            .OrderBy(t => t.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var topicIds = topics.Select(t => t.Id).ToList();
        var translations = await _db.HelpTopicTranslations
            .Where(t => t.IsActive && topicIds.Contains(t.HelpTopicId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var translationsByTopic = translations
            .GroupBy(t => t.HelpTopicId)
            .ToDictionary(g => g.Key, g => g.ToList());

        IReadOnlyList<HelpTopicDto> result = topics.Select(t => Project(t, translationsByTopic)).ToList();
        return Result<IReadOnlyList<HelpTopicDto>>.Success(result);
    }

    /// <inheritdoc />
    public async Task<Result<HelpTopicDto>> GetAsync(string sqid, CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<HelpTopicDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<HelpTopicDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var topic = await _db.HelpTopics
            .SingleOrDefaultAsync(t => t.Id == decoded.Value && t.IsActive, ct)
            .ConfigureAwait(false);
        if (topic is null)
        {
            return Result<HelpTopicDto>.Failure(ErrorCodes.NotFound, "Help topic not found.");
        }

        var translations = await _db.HelpTopicTranslations
            .Where(tr => tr.IsActive && tr.HelpTopicId == topic.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Result<HelpTopicDto>.Success(Project(topic, translations));
    }

    /// <inheritdoc />
    public async Task<Result<HelpTopicDto>> CreateAsync(
        HelpTopicUpsertDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<HelpTopicDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<HelpTopicDto>.Failure(ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var exists = await _db.HelpTopics.AnyAsync(t => t.Code == input.Code, ct).ConfigureAwait(false);
        if (exists)
        {
            return Result<HelpTopicDto>.Failure(
                ErrorCodes.Conflict, $"Help topic with code '{input.Code}' already exists.");
        }

        var now = _clock.UtcNow;
        var topic = new HelpTopic
        {
            Code = input.Code,
            Module = input.Module,
            AnchorSelector = string.IsNullOrWhiteSpace(input.AnchorSelector) ? null : input.AnchorSelector,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.HelpTopics.Add(topic);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result<HelpTopicDto>.Success(Project(topic, new List<HelpTopicTranslation>()));
    }

    /// <inheritdoc />
    public async Task<Result<HelpTopicDto>> UpdateAsync(
        string sqid,
        HelpTopicUpsertDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<HelpTopicDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<HelpTopicDto>.Failure(ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<HelpTopicDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var topic = await _db.HelpTopics
            .SingleOrDefaultAsync(t => t.Id == decoded.Value && t.IsActive, ct)
            .ConfigureAwait(false);
        if (topic is null)
        {
            return Result<HelpTopicDto>.Failure(ErrorCodes.NotFound, "Help topic not found.");
        }

        if (!string.Equals(topic.Code, input.Code, StringComparison.Ordinal))
        {
            return Result<HelpTopicDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Code is immutable — to rename a topic, create a new topic and delete the old one.");
        }

        topic.Module = input.Module;
        topic.AnchorSelector = string.IsNullOrWhiteSpace(input.AnchorSelector) ? null : input.AnchorSelector;
        topic.UpdatedAtUtc = _clock.UtcNow;
        topic.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        var translations = await _db.HelpTopicTranslations
            .Where(tr => tr.IsActive && tr.HelpTopicId == topic.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Result<HelpTopicDto>.Success(Project(topic, translations));
    }

    /// <summary>Projects an entity into the wire DTO using a pre-grouped translation map.</summary>
    /// <param name="topic">Loaded topic entity.</param>
    /// <param name="translationsByTopic">Pre-grouped translation rows keyed by parent id.</param>
    /// <returns>The DTO.</returns>
    private HelpTopicDto Project(
        HelpTopic topic,
        IReadOnlyDictionary<long, List<HelpTopicTranslation>> translationsByTopic)
    {
        translationsByTopic.TryGetValue(topic.Id, out var translations);
        return Project(topic, (IReadOnlyList<HelpTopicTranslation>?)translations ?? Array.Empty<HelpTopicTranslation>());
    }

    /// <summary>Single-topic projection helper.</summary>
    /// <param name="topic">Loaded topic entity.</param>
    /// <param name="translations">Translations for the topic (may be empty).</param>
    /// <returns>The DTO.</returns>
    private HelpTopicDto Project(HelpTopic topic, IReadOnlyList<HelpTopicTranslation> translations)
    {
        var translationDtos = translations
            .OrderBy(t => t.Language, StringComparer.Ordinal)
            .Select(t => new HelpTopicTranslationDto(
                Id: _sqids.Encode(t.Id),
                Language: t.Language,
                Title: t.Title,
                BodyMarkdown: t.BodyMarkdown,
                IsApproved: t.IsApproved,
                TranslatorNote: t.TranslatorNote))
            .ToList();

        return new HelpTopicDto(
            Id: _sqids.Encode(topic.Id),
            Code: topic.Code,
            Module: topic.Module,
            AnchorSelector: topic.AnchorSelector,
            IsActive: topic.IsActive,
            Translations: translationDtos);
    }
}
