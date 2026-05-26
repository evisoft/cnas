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
/// Default <see cref="IAuditPolicyService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Implements the R0182 / SEC 042 contract — every
/// mutation writes a Critical <c>AUDIT.POLICY.{CREATED|UPDATED|DISABLED}</c> audit
/// row and triggers a synchronous refresh of <see cref="AuditPolicyResolver"/>'s
/// in-memory snapshot so the change is visible to the next audit-event write
/// without waiting for the background refresh tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the tech-admin authorization
/// policy; here we only guard against the "service called without an authenticated
/// principal" case via <see cref="ICallerContext.UserId"/> presence.
/// </para>
/// <para>
/// <b>Validation.</b> The service delegates input validation to FluentValidation
/// (<see cref="IValidator{T}"/>) so the rule set is identical between this entry
/// point and any future direct invocation (e.g. a bulk-import tool).
/// </para>
/// </remarks>
public sealed class AuditPolicyService(
    ICnasDbContext db,
    ICallerContext caller,
    ISqidService sqids,
    ICnasTimeProvider clock,
    IAuditService audit,
    AuditPolicyResolver resolver,
    IValidator<AuditPolicyCreateInput> createValidator,
    IValidator<AuditPolicyUpdateInput> updateValidator)
    : IAuditPolicyService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICallerContext _caller = caller;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;
    private readonly AuditPolicyResolver _resolver = resolver;
    private readonly IValidator<AuditPolicyCreateInput> _createValidator = createValidator;
    private readonly IValidator<AuditPolicyUpdateInput> _updateValidator = updateValidator;

    /// <summary>Stable event-code prefix for the audit trail.</summary>
    private const string AuditPrefix = "AUDIT.POLICY";

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AuditPolicyOutput>>> ListAsync(CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<IReadOnlyList<AuditPolicyOutput>>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var rows = await _db.AuditPolicies
            .Where(p => p.IsActive)
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IReadOnlyList<AuditPolicyOutput> items = rows.Select(Project).ToList();
        return Result<IReadOnlyList<AuditPolicyOutput>>.Success(items);
    }

    /// <inheritdoc />
    public async Task<Result<AuditPolicyOutput>> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<AuditPolicyOutput>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            return Result<AuditPolicyOutput>.Failure(ErrorCodes.ValidationFailed, "Code is required.");
        }

        var row = await _db.AuditPolicies
            .SingleOrDefaultAsync(p => p.IsActive && p.Code == code, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<AuditPolicyOutput>.Failure(ErrorCodes.NotFound, "Audit policy not found.");
        }
        return Result<AuditPolicyOutput>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<string>> CreateAsync(AuditPolicyCreateInput input, CancellationToken ct = default)
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

        // Conflict-on-code: the unique index would also surface this on save, but
        // the explicit pre-check returns a tighter error message.
        var existing = await _db.AuditPolicies
            .SingleOrDefaultAsync(p => p.Code == input.Code, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Result<string>.Failure(ErrorCodes.Conflict,
                $"An audit policy already exists with code '{input.Code}'. Update or disable the existing row instead.");
        }

        var now = _clock.UtcNow;
        var row = new AuditPolicy
        {
            Code = input.Code,
            Module = input.Module,
            Screen = input.Screen,
            DataCategory = string.IsNullOrWhiteSpace(input.DataCategory) ? null : input.DataCategory,
            EventCodePattern = input.EventCodePattern,
            OverrideSeverity = ParseSeverity(input.OverrideSeverity),
            SuppressAudit = input.SuppressAudit,
            ExtraRedactKeys = input.ExtraRedactKeys?.ToList() ?? new List<string>(),
            Priority = input.Priority,
            IsEnabled = input.IsEnabled,
            Description = input.Description,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.AuditPolicies.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.CREATED", row, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result<string>.Success(_sqids.Encode(row.Id));
    }

    /// <inheritdoc />
    public async Task<Result> UpdateAsync(string sqid, AuditPolicyUpdateInput input, CancellationToken ct = default)
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

        var row = await _db.AuditPolicies
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Audit policy not found.");
        }

        var now = _clock.UtcNow;
        row.Module = input.Module;
        row.Screen = input.Screen;
        row.DataCategory = string.IsNullOrWhiteSpace(input.DataCategory) ? null : input.DataCategory;
        row.EventCodePattern = input.EventCodePattern;
        row.OverrideSeverity = ParseSeverity(input.OverrideSeverity);
        row.SuppressAudit = input.SuppressAudit;
        row.ExtraRedactKeys = input.ExtraRedactKeys?.ToList() ?? new List<string>();
        row.Priority = input.Priority;
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

        var row = await _db.AuditPolicies
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Audit policy not found.");
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
    /// Centralised so the projection rule is applied identically across all CRUD
    /// surfaces.
    /// </summary>
    /// <param name="row">Loaded entity row.</param>
    /// <returns>The DTO the API surface returns.</returns>
    private AuditPolicyOutput Project(AuditPolicy row) => new(
        Id: _sqids.Encode(row.Id),
        Code: row.Code,
        Module: row.Module,
        Screen: row.Screen,
        DataCategory: row.DataCategory,
        EventCodePattern: row.EventCodePattern,
        OverrideSeverity: row.OverrideSeverity?.ToString(),
        SuppressAudit: row.SuppressAudit,
        ExtraRedactKeys: row.ExtraRedactKeys?.ToList() ?? new List<string>(),
        Priority: row.Priority,
        IsEnabled: row.IsEnabled,
        Description: row.Description);

    /// <summary>
    /// Converts the stable string-form severity used on the Contracts surface back to
    /// the <see cref="AuditSeverity"/> enum stored on the entity. Returns <c>null</c>
    /// when the input is null/whitespace. The validator guarantees a non-null string
    /// is parseable, so the bare <c>Enum.Parse</c> is safe at this layer.
    /// </summary>
    /// <param name="value">String form (<c>Information</c> | <c>Notice</c> | <c>Sensitive</c> | <c>Critical</c>) or null.</param>
    /// <returns>The parsed enum value, or <c>null</c> when no override was supplied.</returns>
    private static AuditSeverity? ParseSeverity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return Enum.Parse<AuditSeverity>(value, ignoreCase: false);
    }

    /// <summary>
    /// Emits a Critical-severity audit row for a policy mutation. Details JSON
    /// carries the natural code + module/screen only — never the regex or the
    /// extra-redact list — so the audit trail is informative without echoing
    /// arbitrary operator-supplied text into the log stream.
    /// </summary>
    /// <param name="eventCode">Stable event code (e.g. <c>AUDIT.POLICY.CREATED</c>).</param>
    /// <param name="row">The persisted (or just-modified) row.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(string eventCode, AuditPolicy row, CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            code = row.Code,
            module = row.Module,
            screen = row.Screen,
            dataCategory = row.DataCategory,
            priority = row.Priority,
            isEnabled = row.IsEnabled,
        });

        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Critical,
            actorId: actor,
            targetEntity: nameof(AuditPolicy),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
