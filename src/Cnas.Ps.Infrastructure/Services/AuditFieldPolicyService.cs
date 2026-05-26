using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IAuditFieldPolicyService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Mirrors the R0182 <c>AuditPolicyService</c>
/// pattern — every mutation writes a Critical
/// <c>AUDIT.FIELDPOLICY.{CREATED|UPDATED|DISABLED}</c> audit row and triggers a
/// synchronous refresh of <see cref="AuditFieldPolicyResolver"/>'s in-memory
/// snapshot so the change is visible to the next diff-write without waiting for
/// the background refresh tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the tech-admin authorization
/// policy; here we only guard against the "service called without an
/// authenticated principal" case via <see cref="ICallerContext.UserId"/>.
/// </para>
/// <para>
/// <b>Validation.</b> The service delegates input validation to FluentValidation
/// (<see cref="IValidator{T}"/>) so the rule set is identical between this entry
/// point and any future direct invocation.
/// </para>
/// </remarks>
public sealed class AuditFieldPolicyService(
    ICnasDbContext db,
    ICallerContext caller,
    ISqidService sqids,
    ICnasTimeProvider clock,
    IAuditService audit,
    AuditFieldPolicyResolver resolver,
    IValidator<AuditFieldPolicyCreateInput> createValidator,
    IValidator<AuditFieldPolicyUpdateInput> updateValidator)
    : IAuditFieldPolicyService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICallerContext _caller = caller;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;
    private readonly AuditFieldPolicyResolver _resolver = resolver;
    private readonly IValidator<AuditFieldPolicyCreateInput> _createValidator = createValidator;
    private readonly IValidator<AuditFieldPolicyUpdateInput> _updateValidator = updateValidator;

    /// <summary>Stable event-code prefix for the audit trail of mutations.</summary>
    private const string AuditPrefix = "AUDIT.FIELDPOLICY";

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AuditFieldPolicyOutput>>> ListAsync(CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<IReadOnlyList<AuditFieldPolicyOutput>>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var rows = await _db.AuditFieldPolicies
            .Where(p => p.IsActive)
            .OrderBy(p => p.EntityType)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IReadOnlyList<AuditFieldPolicyOutput> items = rows.Select(Project).ToList();
        return Result<IReadOnlyList<AuditFieldPolicyOutput>>.Success(items);
    }

    /// <inheritdoc />
    public async Task<Result<AuditFieldPolicyOutput>> GetByEntityTypeAsync(string entityType, CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<AuditFieldPolicyOutput>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return Result<AuditFieldPolicyOutput>.Failure(ErrorCodes.ValidationFailed, "EntityType is required.");
        }

        var row = await _db.AuditFieldPolicies
            .SingleOrDefaultAsync(p => p.IsActive && p.EntityType == entityType, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<AuditFieldPolicyOutput>.Failure(ErrorCodes.NotFound, "Audit field policy not found.");
        }
        return Result<AuditFieldPolicyOutput>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<string>> CreateAsync(AuditFieldPolicyCreateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<string>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var validation = await _createValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<string>.Failure(ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var existing = await _db.AuditFieldPolicies
            .SingleOrDefaultAsync(p => p.EntityType == input.EntityType, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Result<string>.Failure(ErrorCodes.Conflict,
                $"An audit field policy already exists for entity type '{input.EntityType}'. Update or disable the existing row instead.");
        }

        var now = _clock.UtcNow;
        var row = new AuditFieldPolicy
        {
            EntityType = input.EntityType,
            TrackedFields = input.TrackedFields?.ToList() ?? new List<string>(),
            SuppressedFields = input.SuppressedFields?.ToList() ?? new List<string>(),
            RequireAnyChange = input.RequireAnyChange,
            Severity = ParseSeverity(input.Severity),
            IsEnabled = input.IsEnabled,
            Description = input.Description,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.AuditFieldPolicies.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.CREATED", row, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result<string>.Success(_sqids.Encode(row.Id));
    }

    /// <inheritdoc />
    public async Task<Result> UpdateAsync(string sqid, AuditFieldPolicyUpdateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var validation = await _updateValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        var row = await _db.AuditFieldPolicies
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Audit field policy not found.");
        }

        var now = _clock.UtcNow;
        row.TrackedFields = input.TrackedFields?.ToList() ?? new List<string>();
        row.SuppressedFields = input.SuppressedFields?.ToList() ?? new List<string>();
        row.RequireAnyChange = input.RequireAnyChange;
        row.Severity = ParseSeverity(input.Severity);
        row.IsEnabled = input.IsEnabled;
        row.Description = input.Description;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.UPDATED", row, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> DisableAsync(string sqid, CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.AuditFieldPolicies
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Audit field policy not found.");
        }

        var now = _clock.UtcNow;
        row.IsEnabled = false;
        row.IsActive = false;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.DISABLED", row, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Projects the entity into its output DTO with Sqid-encoded identifier.
    /// </summary>
    /// <param name="row">Loaded entity row.</param>
    /// <returns>The DTO the API surface returns.</returns>
    private AuditFieldPolicyOutput Project(AuditFieldPolicy row) => new(
        Id: _sqids.Encode(row.Id),
        EntityType: row.EntityType,
        TrackedFields: row.TrackedFields?.ToList() ?? new List<string>(),
        SuppressedFields: row.SuppressedFields?.ToList() ?? new List<string>(),
        RequireAnyChange: row.RequireAnyChange,
        Severity: row.Severity.ToString(),
        IsEnabled: row.IsEnabled,
        Description: row.Description);

    /// <summary>
    /// Converts the stable string-form severity used on the Contracts surface back
    /// to the <see cref="AuditSeverity"/> enum stored on the entity. The validator
    /// guarantees a parseable value, so the bare <c>Enum.Parse</c> is safe.
    /// </summary>
    /// <param name="value">String form (<c>Information</c>...<c>Critical</c>).</param>
    /// <returns>The parsed enum value.</returns>
    private static AuditSeverity ParseSeverity(string value)
        => Enum.Parse<AuditSeverity>(value, ignoreCase: false);

    /// <summary>
    /// Emits a Critical-severity audit row for a policy mutation. Details JSON
    /// carries the EntityType + tracked-field count only — never the actual field
    /// lists — so the audit trail is informative without echoing arbitrary
    /// operator-supplied text into the log stream.
    /// </summary>
    /// <param name="eventCode">Stable event code (e.g. <c>AUDIT.FIELDPOLICY.CREATED</c>).</param>
    /// <param name="row">The persisted (or just-modified) row.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(string eventCode, AuditFieldPolicy row, CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            entityType = row.EntityType,
            trackedCount = row.TrackedFields?.Count ?? 0,
            suppressedCount = row.SuppressedFields?.Count ?? 0,
            requireAnyChange = row.RequireAnyChange,
            severity = row.Severity.ToString(),
            isEnabled = row.IsEnabled,
        });

        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Critical,
            actorId: actor,
            targetEntity: nameof(AuditFieldPolicy),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
