using System.Linq;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IWorkflowNotificationStrategyService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Every mutation writes a Critical
/// <c>WORKFLOW.NOTIFY.STRATEGY.{CREATED|UPDATED|DISABLED}</c> audit row and triggers a
/// synchronous refresh of <see cref="WorkflowNotificationStrategyResolver"/>'s
/// in-memory snapshot so the change is visible to the next workflow dispatch without
/// waiting for the background refresh tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation seam.</b> The controller applies the workflow-management policy;
/// here we only guard against the "service called without an authenticated principal"
/// case via <see cref="ICallerContext.UserId"/>.
/// </para>
/// <para>
/// <b>Idempotent upsert.</b> <see cref="UpsertAsync"/> inserts on first call and updates
/// thereafter; the natural-key UNIQUE on (WorkflowDefinitionId, EventCode) prevents
/// duplicates. The audit row captures CREATED vs UPDATED so investigators can tell
/// which path ran.
/// </para>
/// </remarks>
public sealed class WorkflowNotificationStrategyService(
    ICnasDbContext db,
    ICallerContext caller,
    ISqidService sqids,
    ICnasTimeProvider clock,
    IAuditService audit,
    WorkflowNotificationStrategyResolver resolver,
    IValidator<WorkflowNotificationStrategyUpsertInput> upsertValidator)
    : IWorkflowNotificationStrategyService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICallerContext _caller = caller;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;
    private readonly WorkflowNotificationStrategyResolver _resolver = resolver;
    private readonly IValidator<WorkflowNotificationStrategyUpsertInput> _upsertValidator = upsertValidator;

    /// <summary>Stable audit-event prefix.</summary>
    private const string AuditPrefix = "WORKFLOW.NOTIFY.STRATEGY";

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<WorkflowNotificationStrategyOutput>>> ListAsync(
        string workflowSqid,
        CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<IReadOnlyList<WorkflowNotificationStrategyOutput>>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(workflowSqid);
        if (decoded.IsFailure)
        {
            return Result<IReadOnlyList<WorkflowNotificationStrategyOutput>>.Failure(
                decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var rows = await _db.WorkflowNotificationStrategies
            .Where(s => s.WorkflowDefinitionId == decoded.Value && s.IsActive)
            .OrderBy(s => s.EventCode)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IReadOnlyList<WorkflowNotificationStrategyOutput> items = rows.Select(Project).ToList();
        return Result<IReadOnlyList<WorkflowNotificationStrategyOutput>>.Success(items);
    }

    /// <inheritdoc />
    public async Task<Result<WorkflowNotificationStrategyOutput>> GetByEventAsync(
        string workflowSqid,
        string eventCode,
        CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result<WorkflowNotificationStrategyOutput>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        if (!WorkflowNotificationStrategyUpsertInputValidator.EventCodeIsKnown(eventCode))
        {
            return Result<WorkflowNotificationStrategyOutput>.Failure(
                ErrorCodes.ValidationFailed,
                $"Unknown event code '{eventCode}'. Must be one of: {string.Join(", ", WorkflowNotificationEvents.All)}.");
        }

        var decoded = _sqids.TryDecode(workflowSqid);
        if (decoded.IsFailure)
        {
            return Result<WorkflowNotificationStrategyOutput>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.WorkflowNotificationStrategies
            .SingleOrDefaultAsync(
                s => s.WorkflowDefinitionId == decoded.Value && s.EventCode == eventCode && s.IsActive,
                ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<WorkflowNotificationStrategyOutput>.Failure(
                ErrorCodes.NotFound, "Workflow notification strategy not found.");
        }
        return Result<WorkflowNotificationStrategyOutput>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<WorkflowNotificationStrategyOutput>> UpsertAsync(
        string workflowSqid,
        string eventCode,
        WorkflowNotificationStrategyUpsertInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is null)
        {
            return Result<WorkflowNotificationStrategyOutput>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        if (!WorkflowNotificationStrategyUpsertInputValidator.EventCodeIsKnown(eventCode))
        {
            return Result<WorkflowNotificationStrategyOutput>.Failure(
                ErrorCodes.ValidationFailed,
                $"Unknown event code '{eventCode}'. Must be one of: {string.Join(", ", WorkflowNotificationEvents.All)}.");
        }

        var decoded = _sqids.TryDecode(workflowSqid);
        if (decoded.IsFailure)
        {
            return Result<WorkflowNotificationStrategyOutput>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var validation = await _upsertValidator.ValidateAsync(input, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Result<WorkflowNotificationStrategyOutput>.Failure(
                ErrorCodes.ValidationFailed, validation.ToString("; "));
        }

        // Verify the workflow definition exists (any version). Strategies are bound to
        // the workflow Id; a typo on the route surfaces as NotFound rather than a
        // silent foreign-key reference to a missing row.
        var workflowExists = await _db.WorkflowDefinitions
            .AnyAsync(w => w.Id == decoded.Value, ct)
            .ConfigureAwait(false);
        if (!workflowExists)
        {
            return Result<WorkflowNotificationStrategyOutput>.Failure(
                ErrorCodes.NotFound, "Workflow definition not found.");
        }

        var now = _clock.UtcNow;
        var existing = await _db.WorkflowNotificationStrategies
            .SingleOrDefaultAsync(
                s => s.WorkflowDefinitionId == decoded.Value && s.EventCode == eventCode,
                ct)
            .ConfigureAwait(false);

        WorkflowNotificationStrategy row;
        bool created;
        if (existing is null)
        {
            row = new WorkflowNotificationStrategy
            {
                WorkflowDefinitionId = decoded.Value,
                EventCode = eventCode,
                IsEnabled = input.IsEnabled,
                Channels = ParseChannels(input.Channels),
                RecipientRoles = input.RecipientRoles?.ToList() ?? new List<string>(),
                TemplateCodeOverride = string.IsNullOrWhiteSpace(input.TemplateCodeOverride)
                    ? null
                    : input.TemplateCodeOverride,
                QuietHoursStartLocalMinute = input.QuietHoursStart,
                QuietHoursEndLocalMinute = input.QuietHoursEnd,
                Description = input.Description,
                CreatedAtUtc = now,
                CreatedBy = _caller.UserSqid,
                IsActive = true,
            };
            _db.WorkflowNotificationStrategies.Add(row);
            created = true;
        }
        else
        {
            row = existing;
            row.IsEnabled = input.IsEnabled;
            row.Channels = ParseChannels(input.Channels);
            row.RecipientRoles = input.RecipientRoles?.ToList() ?? new List<string>();
            row.TemplateCodeOverride = string.IsNullOrWhiteSpace(input.TemplateCodeOverride)
                ? null
                : input.TemplateCodeOverride;
            row.QuietHoursStartLocalMinute = input.QuietHoursStart;
            row.QuietHoursEndLocalMinute = input.QuietHoursEnd;
            row.Description = input.Description;
            row.IsActive = true;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            created = false;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var eventName = created ? $"{AuditPrefix}.CREATED" : $"{AuditPrefix}.UPDATED";
        await EmitAuditAsync(eventName, row, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result<WorkflowNotificationStrategyOutput>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result> DisableAsync(string workflowSqid, string eventCode, CancellationToken ct = default)
    {
        if (_caller.UserId is null)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }
        if (!WorkflowNotificationStrategyUpsertInputValidator.EventCodeIsKnown(eventCode))
        {
            return Result.Failure(
                ErrorCodes.ValidationFailed,
                $"Unknown event code '{eventCode}'. Must be one of: {string.Join(", ", WorkflowNotificationEvents.All)}.");
        }

        var decoded = _sqids.TryDecode(workflowSqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.WorkflowNotificationStrategies
            .SingleOrDefaultAsync(
                s => s.WorkflowDefinitionId == decoded.Value && s.EventCode == eventCode && s.IsActive,
                ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Workflow notification strategy not found.");
        }

        row.IsActive = false;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.DISABLED", row, ct).ConfigureAwait(false);
        await _resolver.InvalidateAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Projects the entity into its output DTO with Sqid-encoded identifiers and
    /// stable string channel names. Centralised so the projection rule is applied
    /// identically across every read path.
    /// </summary>
    /// <param name="row">Loaded entity row.</param>
    /// <returns>The DTO the API surface returns.</returns>
    private WorkflowNotificationStrategyOutput Project(WorkflowNotificationStrategy row) => new(
        Id: _sqids.Encode(row.Id),
        WorkflowDefinitionId: _sqids.Encode(row.WorkflowDefinitionId),
        EventCode: row.EventCode,
        IsEnabled: row.IsEnabled,
        Channels: (row.Channels ?? new List<NotificationChannel>()).Select(c => c.ToString()).ToList(),
        RecipientRoles: row.RecipientRoles?.ToList() ?? new List<string>(),
        TemplateCodeOverride: row.TemplateCodeOverride,
        QuietHoursStart: row.QuietHoursStartLocalMinute,
        QuietHoursEnd: row.QuietHoursEndLocalMinute,
        Description: row.Description);

    /// <summary>
    /// Parses stable channel strings to <see cref="NotificationChannel"/> enum values.
    /// The validator pre-checks each entry; this method assumes validity and throws
    /// on a contract violation (which would be a programming error).
    /// </summary>
    /// <param name="strings">Validated channel strings from the DTO.</param>
    /// <returns>The parsed enum list (never null).</returns>
    private static List<NotificationChannel> ParseChannels(IReadOnlyList<string>? strings)
    {
        if (strings is null || strings.Count == 0)
        {
            return new List<NotificationChannel>();
        }
        var result = new List<NotificationChannel>(strings.Count);
        foreach (var s in strings)
        {
            result.Add(Enum.Parse<NotificationChannel>(s, ignoreCase: false));
        }
        return result;
    }

    /// <summary>
    /// Emits a Critical-severity audit row for a strategy mutation. The details JSON
    /// captures the natural key (workflowDefinitionId, eventCode) + the enable / channel
    /// shape so investigators can reconstruct the change without echoing the entire
    /// payload (which may carry long descriptions).
    /// </summary>
    /// <param name="eventCode">Stable audit event code (e.g. <c>WORKFLOW.NOTIFY.STRATEGY.CREATED</c>).</param>
    /// <param name="row">The persisted (or just-modified) row.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(string eventCode, WorkflowNotificationStrategy row, CancellationToken ct)
    {
        var details = JsonSerializer.Serialize(new
        {
            workflowDefinitionId = _sqids.Encode(row.WorkflowDefinitionId),
            eventCode = row.EventCode,
            isEnabled = row.IsEnabled,
            channelCount = row.Channels?.Count ?? 0,
            recipientRoleCount = row.RecipientRoles?.Count ?? 0,
            hasTemplateOverride = !string.IsNullOrEmpty(row.TemplateCodeOverride),
            hasQuietHours = row.QuietHoursStartLocalMinute is not null,
        });

        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Critical,
            actorId: actor,
            targetEntity: nameof(WorkflowNotificationStrategy),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
