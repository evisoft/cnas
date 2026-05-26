using System.Collections.Generic;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Application.BulkActions.Operations;

/// <summary>
/// R0527 / TOR CF 03.11 / UI 015 — bulk operation that flips the
/// <see cref="ServiceApplication.Status"/> of every selected Cerere to a new status.
/// Parameter shape: <c>{ "newStatus": "Approved" }</c>. Rejects rows that have already
/// reached a terminal state (<see cref="ApplicationStatus.Closed"/>,
/// <see cref="ApplicationStatus.Rejected"/>, <see cref="ApplicationStatus.Withdrawn"/>)
/// with a stable <c>INVALID_TRANSITION</c> per-row failure code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reachable target states.</b> The operation accepts the same set of statuses
/// declared in <see cref="ApplicationStatus"/> — the per-row guard prevents leaving a
/// terminal status (the lifecycle policy enforced by
/// <see cref="ServiceApplication.TransitionStatus(ApplicationStatus, System.DateTime)"/>).
/// </para>
/// <para>
/// <b>Audit emission.</b> Emits one <c>CERERE.STATUS_CHANGED</c> audit row (severity
/// Notice) per successfully transitioned row. The runner wraps the per-row audits with
/// its own STARTED/COMPLETED rows.
/// </para>
/// </remarks>
public sealed class CerereChangeStatusBulkOperation : IBulkOperation
{
    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly IAuditService _audit;

    /// <summary>Creates the operation.</summary>
    /// <param name="db">Per-request CNAS DbContext.</param>
    /// <param name="clock">UTC clock used to stamp <c>UpdatedAtUtc</c> + audit timestamps.</param>
    /// <param name="audit">Audit-service façade used for per-row CERERE.STATUS_CHANGED rows.</param>
    public CerereChangeStatusBulkOperation(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        IAuditService audit)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(audit);

        _db = db;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>Stable operation code.</summary>
    public const string OperationCode = "Cerere.ChangeStatus";

    /// <summary>
    /// Cached <see cref="JsonSerializerOptions"/> reused on every row to satisfy the
    /// CA1869 analyzer guidance — building a fresh options instance per deserialise
    /// call is wasteful when the shape never changes.
    /// </summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Terminal statuses — the operation refuses transitions out of any of these. The
    /// set mirrors the implicit invariant in
    /// <see cref="ServiceApplication.TransitionStatus(ApplicationStatus, System.DateTime)"/>
    /// (the helper does NOT reset bookkeeping for an exit from a terminal status).
    /// </summary>
    private static readonly HashSet<ApplicationStatus> TerminalStatuses = new()
    {
        ApplicationStatus.Closed,
        ApplicationStatus.Rejected,
        ApplicationStatus.Withdrawn,
    };

    /// <inheritdoc />
    public string Code => OperationCode;

    /// <inheritdoc />
    public string Registry => BulkRegistries.Cerere;

    /// <inheritdoc />
    public string RequiredPermission => "Cerere.Manage";

    /// <inheritdoc />
    public int MaxRowsPerRun => 500;

    /// <inheritdoc />
    public bool RequiresParameters => true;

    /// <inheritdoc />
    public async Task<BulkRowOutcome> ExecuteAsync(
        long rowId,
        string? parametersJson,
        ICallerContext caller,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // Re-parse the parameters per row — cheap, stateless, and means a bad shape
        // surfaces as a per-row failure rather than aborting the whole run.
        Parameters? parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<Parameters>(
                parametersJson ?? "null",
                CachedJsonOptions);
        }
        catch (JsonException ex)
        {
            return BulkRowOutcome.Failed(ErrorCodes.ValidationFailed, $"Parameters JSON malformed: {ex.Message}");
        }
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.NewStatus))
        {
            return BulkRowOutcome.Failed(
                ErrorCodes.ValidationFailed,
                "Parameters must carry a non-empty newStatus.");
        }
        if (!Enum.TryParse<ApplicationStatus>(parameters.NewStatus, ignoreCase: true, out var newStatus))
        {
            return BulkRowOutcome.Failed(
                ErrorCodes.ValidationFailed,
                $"newStatus '{parameters.NewStatus}' is not a member of ApplicationStatus.");
        }

        var cerere = await _db.Applications
            .SingleOrDefaultAsync(a => a.Id == rowId && a.IsActive, ct)
            .ConfigureAwait(false);
        if (cerere is null)
        {
            return BulkRowOutcome.Failed(ErrorCodes.NotFound, "Cerere not found.");
        }

        // No-op transition — surface as success so the run-level counters reflect
        // "row already in target state" rather than spurious failure noise.
        if (cerere.Status == newStatus)
        {
            return BulkRowOutcome.Succeeded();
        }

        // Reject exits from a terminal status (the lifecycle helper would otherwise
        // happily flip Closed -> Submitted, breaking the immutable-final invariant).
        if (TerminalStatuses.Contains(cerere.Status))
        {
            return BulkRowOutcome.Failed(
                "INVALID_TRANSITION",
                $"Cerere is in terminal status {cerere.Status}; status change refused.");
        }

        var now = _clock.UtcNow;
        var previousStatus = cerere.Status;
        cerere.TransitionStatus(newStatus, now);
        cerere.UpdatedAtUtc = now;
        cerere.UpdatedBy = caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var detailsJson = JsonSerializer.Serialize(new
        {
            previousStatus = previousStatus.ToString(),
            newStatus = newStatus.ToString(),
        });
        await _audit.RecordAsync(
            eventCode: "CERERE.STATUS_CHANGED",
            severity: AuditSeverity.Notice,
            actorId: caller.UserSqid ?? "system",
            targetEntity: nameof(ServiceApplication),
            targetEntityId: cerere.Id,
            detailsJson: detailsJson,
            sourceIp: caller.SourceIp,
            correlationId: caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        return BulkRowOutcome.Succeeded();
    }

    /// <summary>
    /// JSON-deserialisable shape of the operation's parameters payload. Internal —
    /// callers post the JSON directly; this type only exists to bind it.
    /// </summary>
    /// <param name="NewStatus">Stable PascalCase <see cref="ApplicationStatus"/> name.</param>
    private sealed record Parameters(string? NewStatus);
}
