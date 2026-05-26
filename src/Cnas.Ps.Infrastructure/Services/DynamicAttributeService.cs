using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R2190-R2200 / TOR §15.6 FLEX 006 — implementation of
/// <see cref="IDynamicAttributeService"/>. Backs the dynamic-attributes EAV
/// sidecar over existing core entities.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid contract.</b> Every external <c>EntitySqid</c> input is decoded via
/// <see cref="ISqidService.TryDecode(string?)"/> at the boundary; the internal
/// 64-bit <see cref="EntityAttributeValue.EntityId"/> is the only form the DB
/// ever sees. Output DTOs re-encode the surrogate ids on the way out.
/// </para>
/// <para>
/// <b>Allow-list gate.</b> <see cref="IDynamicAttributeService.AllowedAttributeCodes"/>
/// is consulted at the service-layer entry; unknown codes are rejected with
/// <see cref="ErrorCodes.ValidationFailed"/> before any database round-trip.
/// </para>
/// <para>
/// <b>Upsert semantics.</b> <see cref="SetAsync"/> probes the unique
/// (EntityType, EntityId, AttributeCode) tuple; first observation inserts a
/// fresh row, subsequent observations update only the <c>Value</c> column.
/// Byte-equal values still bump <see cref="AuditableEntity.UpdatedAtUtc"/> so
/// dashboards can see the "touched" timestamp.
/// </para>
/// </remarks>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder used at the input + output boundaries.</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly (CLAUDE.md).</param>
/// <param name="caller">Authenticated caller — supplies the actor id for audit-row stamps.</param>
public sealed class DynamicAttributeService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller) : IDynamicAttributeService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;

    /// <summary>Maximum length of a stored attribute value — must match the EF mapping.</summary>
    private const int MaxValueLength = 4096;

    /// <summary>Maximum length of an attribute-code / entity-type key — must match the EF mapping.</summary>
    private const int MaxCodeLength = 64;

    /// <inheritdoc />
    public async Task<Result<EntityAttributeValueDto>> SetAsync(
        SetEntityAttributeInputDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var validation = ValidateShape(input.EntityType, input.AttributeCode, input.Value);
        if (validation.IsFailure)
        {
            return Result<EntityAttributeValueDto>.Failure(validation.ErrorCode!, validation.ErrorMessage!);
        }

        var decoded = _sqids.TryDecode(input.EntitySqid);
        if (decoded.IsFailure)
        {
            return Result<EntityAttributeValueDto>.Failure(ErrorCodes.NotFound,
                $"Entity '{input.EntitySqid}' cannot be resolved.");
        }
        var entityId = decoded.Value;

        var entityType = input.EntityType.Trim();
        var attributeCode = input.AttributeCode.Trim();

        var existing = await _db.EntityAttributeValues
            .FirstOrDefaultAsync(
                e => e.EntityType == entityType
                    && e.EntityId == entityId
                    && e.AttributeCode == attributeCode,
                cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        EntityAttributeValue row;
        if (existing is null)
        {
            row = new EntityAttributeValue
            {
                EntityType = entityType,
                EntityId = entityId,
                AttributeCode = attributeCode,
                Value = input.Value,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
            };
            _db.EntityAttributeValues.Add(row);
        }
        else
        {
            existing.Value = input.Value;
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = _caller.UserSqid;
            row = existing;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result<EntityAttributeValueDto>.Success(MapToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<EntityAttributeValueDto>> GetAsync(
        string entityType,
        string entitySqid,
        string attributeCode,
        CancellationToken cancellationToken = default)
    {
        var shape = ValidateShape(entityType, attributeCode, value: null);
        if (shape.IsFailure)
        {
            return Result<EntityAttributeValueDto>.Failure(shape.ErrorCode!, shape.ErrorMessage!);
        }

        var decoded = _sqids.TryDecode(entitySqid);
        if (decoded.IsFailure)
        {
            return Result<EntityAttributeValueDto>.Failure(ErrorCodes.NotFound,
                $"Entity '{entitySqid}' cannot be resolved.");
        }
        var entityId = decoded.Value;
        var typeKey = entityType.Trim();
        var codeKey = attributeCode.Trim();

        var row = await _db.EntityAttributeValues
            .FirstOrDefaultAsync(
                e => e.EntityType == typeKey
                    && e.EntityId == entityId
                    && e.AttributeCode == codeKey,
                cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? Result<EntityAttributeValueDto>.Failure(ErrorCodes.NotFound,
                $"No dynamic attribute '{codeKey}' on {typeKey}:{entitySqid}.")
            : Result<EntityAttributeValueDto>.Success(MapToDto(row));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<EntityAttributeValueDto>>> ListAsync(
        string entityType,
        string entitySqid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return Result<IReadOnlyList<EntityAttributeValueDto>>.Failure(
                ErrorCodes.ValidationFailed, "EntityType is required.");
        }

        var decoded = _sqids.TryDecode(entitySqid);
        if (decoded.IsFailure)
        {
            return Result<IReadOnlyList<EntityAttributeValueDto>>.Failure(
                ErrorCodes.NotFound,
                $"Entity '{entitySqid}' cannot be resolved.");
        }
        var entityId = decoded.Value;
        var typeKey = entityType.Trim();

        var rows = await _db.EntityAttributeValues
            .Where(e => e.EntityType == typeKey && e.EntityId == entityId)
            .OrderBy(e => e.AttributeCode)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<EntityAttributeValueDto> ro = rows
            .Select(MapToDto)
            .ToList();
        return Result<IReadOnlyList<EntityAttributeValueDto>>.Success(ro);
    }

    /// <summary>
    /// Validates the shape of an inbound attribute write/read — entity-type
    /// length, attribute-code allow-list, value length. Returns a failure
    /// <see cref="Result"/> with <see cref="ErrorCodes.ValidationFailed"/> on
    /// rejection.
    /// </summary>
    /// <param name="entityType">Logical entity kind (required).</param>
    /// <param name="attributeCode">Allow-listed attribute code (required).</param>
    /// <param name="value">Inbound value, or <c>null</c> on read paths (skipped).</param>
    /// <returns>Success when every gate passes; failure otherwise.</returns>
    private static Result ValidateShape(string entityType, string attributeCode, string? value)
    {
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "EntityType is required.");
        }
        if (entityType.Length > MaxCodeLength)
        {
            return Result.Failure(ErrorCodes.ValidationFailed,
                $"EntityType must be ≤ {MaxCodeLength} chars.");
        }
        if (string.IsNullOrWhiteSpace(attributeCode))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "AttributeCode is required.");
        }
        if (!IDynamicAttributeService.AllowedAttributeCodes.Contains(attributeCode.Trim()))
        {
            return Result.Failure(ErrorCodes.ValidationFailed,
                $"AttributeCode '{attributeCode}' is not in the allow-list.");
        }
        if (value is not null)
        {
            if (value.Length > MaxValueLength)
            {
                return Result.Failure(ErrorCodes.ValidationFailed,
                    $"Value must be ≤ {MaxValueLength} chars.");
            }
        }
        return Result.Success();
    }

    /// <summary>
    /// Projects a persisted <see cref="EntityAttributeValue"/> row to its
    /// outbound <see cref="EntityAttributeValueDto"/> form, re-encoding the
    /// surrogate ids through <see cref="ISqidService.Encode(long)"/>.
    /// </summary>
    /// <param name="row">Persisted row.</param>
    /// <returns>Sqid-encoded outbound DTO.</returns>
    private EntityAttributeValueDto MapToDto(EntityAttributeValue row) =>
        new(
            Id: _sqids.Encode(row.Id),
            EntityType: row.EntityType,
            EntitySqid: _sqids.Encode(row.EntityId),
            AttributeCode: row.AttributeCode,
            Value: row.Value);
}
