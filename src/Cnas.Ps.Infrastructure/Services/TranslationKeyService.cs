using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Localization;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="ITranslationKeyService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Mutations are non-Critical (operators tune copy
/// frequently; flooding the audit explorer with key-metadata edits would drown the
/// signal); the per-language value side service emits the Critical
/// <c>TRANSLATION.APPROVED</c> audit row instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the <c>CnasAdmin</c> policy;
/// the service only guards the "service called without an authenticated principal"
/// case via <see cref="ICallerContext.UserId"/>.
/// </para>
/// </remarks>
public sealed class TranslationKeyService : ITranslationKeyService
{
    private readonly ICnasDbContext _db;
    private readonly ICallerContext _caller;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly IValidator<TranslationKeyUpsertDto> _validator;

    /// <summary>Constructs the service with its DI dependencies.</summary>
    /// <param name="db">Per-request DbContext.</param>
    /// <param name="caller">Per-request caller context.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC time provider.</param>
    /// <param name="validator">Body validator for <see cref="TranslationKeyUpsertDto"/>.</param>
    public TranslationKeyService(
        ICnasDbContext db,
        ICallerContext caller,
        ISqidService sqids,
        ICnasTimeProvider clock,
        IValidator<TranslationKeyUpsertDto> validator)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(validator);
        _db = db;
        _caller = caller;
        _sqids = sqids;
        _clock = clock;
        _validator = validator;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<TranslationKeyDto>>> ListAsync(
        string? module,
        CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<IReadOnlyList<TranslationKeyDto>>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var keys = await _db.TranslationKeys
            .Where(k => k.IsActive)
            .Where(k => module == null || k.Module == module)
            .OrderBy(k => k.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var keyIds = keys.Select(k => k.Id).ToList();
        var values = await _db.TranslationValues
            .Where(v => v.IsActive && keyIds.Contains(v.TranslationKeyId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var valuesByKey = values
            .GroupBy(v => v.TranslationKeyId)
            .ToDictionary(g => g.Key, g => g.ToList());

        IReadOnlyList<TranslationKeyDto> result = keys.Select(k => Project(k, valuesByKey)).ToList();
        return Result<IReadOnlyList<TranslationKeyDto>>.Success(result);
    }

    /// <inheritdoc />
    public async Task<Result<TranslationKeyDto>> GetAsync(string sqid, CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<TranslationKeyDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<TranslationKeyDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var key = await _db.TranslationKeys
            .SingleOrDefaultAsync(k => k.Id == decoded.Value && k.IsActive, ct)
            .ConfigureAwait(false);
        if (key is null)
        {
            return Result<TranslationKeyDto>.Failure(ErrorCodes.NotFound, "Translation key not found.");
        }

        var values = await _db.TranslationValues
            .Where(v => v.IsActive && v.TranslationKeyId == key.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Result<TranslationKeyDto>.Success(Project(key, values));
    }

    /// <inheritdoc />
    public async Task<Result<TranslationKeyDto>> CreateAsync(
        TranslationKeyUpsertDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<TranslationKeyDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<TranslationKeyDto>.Failure(ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var exists = await _db.TranslationKeys.AnyAsync(k => k.Code == input.Code, ct).ConfigureAwait(false);
        if (exists)
        {
            return Result<TranslationKeyDto>.Failure(
                ErrorCodes.Conflict, $"Translation key with code '{input.Code}' already exists.");
        }

        var now = _clock.UtcNow;
        var key = new TranslationKey
        {
            Code = input.Code,
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description,
            Module = string.IsNullOrWhiteSpace(input.Module) ? null : input.Module,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.TranslationKeys.Add(key);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Result<TranslationKeyDto>.Success(Project(key, new List<TranslationValue>()));
    }

    /// <inheritdoc />
    public async Task<Result<TranslationKeyDto>> UpdateAsync(
        string sqid,
        TranslationKeyUpsertDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<TranslationKeyDto>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var validation = await _validator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<TranslationKeyDto>.Failure(ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<TranslationKeyDto>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var key = await _db.TranslationKeys
            .SingleOrDefaultAsync(k => k.Id == decoded.Value && k.IsActive, ct)
            .ConfigureAwait(false);
        if (key is null)
        {
            return Result<TranslationKeyDto>.Failure(ErrorCodes.NotFound, "Translation key not found.");
        }

        if (!string.Equals(key.Code, input.Code, StringComparison.Ordinal))
        {
            return Result<TranslationKeyDto>.Failure(
                ErrorCodes.ValidationFailed,
                "Code is immutable — to rename a key, create a new key and delete the old one.");
        }

        key.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description;
        key.Module = string.IsNullOrWhiteSpace(input.Module) ? null : input.Module;
        key.UpdatedAtUtc = _clock.UtcNow;
        key.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var values = await _db.TranslationValues
            .Where(v => v.IsActive && v.TranslationKeyId == key.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Result<TranslationKeyDto>.Success(Project(key, values));
    }

    /// <summary>
    /// Projects the entity + its values into the wire DTO. Centralised so the
    /// projection rule is applied identically across every read path.
    /// </summary>
    /// <param name="key">Loaded key entity.</param>
    /// <param name="valuesByKey">Pre-grouped value rows keyed by parent id.</param>
    /// <returns>The DTO the API surface returns.</returns>
    private TranslationKeyDto Project(
        TranslationKey key,
        IReadOnlyDictionary<long, List<TranslationValue>> valuesByKey)
    {
        valuesByKey.TryGetValue(key.Id, out var values);
        return Project(key, (IReadOnlyList<TranslationValue>?)values ?? Array.Empty<TranslationValue>());
    }

    /// <summary>Single-key projection helper used by all read paths.</summary>
    /// <param name="key">Loaded key entity.</param>
    /// <param name="values">Values for the key (may be empty).</param>
    /// <returns>The DTO.</returns>
    private TranslationKeyDto Project(TranslationKey key, IReadOnlyList<TranslationValue> values)
    {
        var valueDtos = values
            .OrderBy(v => v.Language, StringComparer.Ordinal)
            .Select(v => new TranslationValueDto(
                Id: _sqids.Encode(v.Id),
                Language: v.Language,
                Text: v.Text,
                IsApproved: v.IsApproved,
                TranslatorNote: v.TranslatorNote))
            .ToList();

        return new TranslationKeyDto(
            Id: _sqids.Encode(key.Id),
            Code: key.Code,
            Description: key.Description,
            Module: key.Module,
            Values: valueDtos);
    }
}
