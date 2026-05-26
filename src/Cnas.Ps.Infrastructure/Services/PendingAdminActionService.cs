using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="IPendingAdminActionService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Implements the maker-checker / 4-eyes workflow for
/// sensitive admin actions (R0058 / SEC 027) — see the interface XML doc for the full
/// contract. Holds an <see cref="IEnumerable{T}"/> of
/// <see cref="IPendingAdminActionExecutor"/> registrations and dispatches to the first
/// executor whose <c>Handles</c> returns <c>true</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Operation-code validation at submit time.</b> Unknown operation codes are rejected
/// immediately (rather than at approve time) so the queue cannot accumulate actions
/// that no executor would ever apply.
/// </para>
/// <para>
/// <b>TTL guard at approve time.</b> Even though a background sweeper expires stale
/// rows, the approve path still re-checks <c>ExpiresAtUtc &lt; now</c> and flips the
/// row to <see cref="PendingAdminActionStatus.Expired"/> inline so an approval that
/// races with the sweeper still produces a deterministic, single-decision audit trail.
/// </para>
/// <para>
/// <b>Idempotent approval.</b> If two checker calls race, the second sees the
/// already-approved row and returns
/// <see cref="ErrorCodes.MakerCheckerAlreadyDecided"/> without re-invoking the
/// executor.
/// </para>
/// </remarks>
public sealed class PendingAdminActionService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IEnumerable<IPendingAdminActionExecutor> executors)
    : IPendingAdminActionService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;

    /// <summary>Materialised once so multiple operations share a stable dispatch table.</summary>
    private readonly IReadOnlyList<IPendingAdminActionExecutor> _executors = executors.ToList();

    /// <summary>Default TTL applied when the caller does not override it.</summary>
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    /// <summary>Floor applied when the caller passes a non-positive override TTL.</summary>
    private static readonly TimeSpan MinTtl = TimeSpan.FromMinutes(1);

    /// <summary>Defense-in-depth role guard mirroring <c>UserAdministrationService</c>.</summary>
    private const string AdminRole = "cnas-admin";

    /// <inheritdoc />
    public async Task<Result<string>> SubmitAsync(string operation, string payloadJson, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(payloadJson);

        // Defense-in-depth — the controller's [Authorize(Policy = CnasAdmin)] attribute
        // is the primary gate, but internal callers (background jobs, future MediatR
        // pipelines) could bypass it. Re-check at the service boundary.
        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result<string>.Failure(ErrorCodes.Forbidden, "Caller lacks cnas-admin role.");
        }

        // Maker identity is required — the row is meaningless without it.
        if (_caller.UserId is not long makerId)
        {
            return Result<string>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Fail-fast on unknown operations so the queue cannot accumulate undeliverable
        // actions. The same Handles check is repeated at approve time as a sanity
        // guard, but rejecting here means the maker gets immediate feedback.
        if (!_executors.Any(e => e.Handles(operation)))
        {
            return Result<string>.Failure(
                ErrorCodes.MakerCheckerUnknownOperation,
                $"No executor registered for operation '{operation}'.");
        }

        var now = _clock.UtcNow;
        var effectiveTtl = ttl is { } supplied
            ? (supplied <= TimeSpan.Zero ? MinTtl : supplied)
            : DefaultTtl;

        var row = new PendingAdminAction
        {
            Operation = operation,
            PayloadJson = payloadJson,
            MakerUserId = makerId,
            MakerRequestedAtUtc = now,
            Status = PendingAdminActionStatus.Pending,
            ExpiresAtUtc = now + effectiveTtl,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.PendingAdminActions.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0040 — counter incremented AFTER persistence so a SaveChanges throw above
        // propagates without inflating the success rate.
        CnasMeter.AdminActionSubmitted.Add(1);

        return Result<string>.Success(_sqids.Encode(row.Id));
    }

    /// <inheritdoc />
    public async Task<Result> ApproveAsync(string actionSqid, CancellationToken ct = default)
    {
        var loaded = await LoadAndGuardAsync(actionSqid, ct).ConfigureAwait(false);
        if (loaded.Failure is { } failure) return failure;
        var row = loaded.Row!;

        // Maker ≠ checker — the very point of the 4-eyes ceremony.
        if (_caller.UserId is long checkerId && checkerId == row.MakerUserId)
        {
            return Result.Failure(
                ErrorCodes.MakerCheckerSelfApprovalForbidden,
                "Maker cannot approve their own action.");
        }
        if (_caller.UserId is not long approverId)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Flip status FIRST (and save) so a racing duplicate approve sees the new state
        // and short-circuits. The executor runs only after the row has been atomically
        // claimed for this checker.
        var now = _clock.UtcNow;
        row.Status = PendingAdminActionStatus.Approved;
        row.CheckerUserId = approverId;
        row.CheckerDecidedAtUtc = now;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0040 — count the approval BEFORE the executor runs so a flaky executor
        // doesn't suppress the maker-checker decision metric. The executor result has
        // its own observability surface (logs, downstream metrics) and is out of
        // scope for the 4-eyes counter.
        CnasMeter.AdminActionApproved.Add(1);

        // Dispatch to the executor that handles this operation. The submit path already
        // verified at least one match exists; re-resolve here in case the registered
        // set changed between submit and approve.
        var executor = _executors.FirstOrDefault(e => e.Handles(row.Operation));
        if (executor is null)
        {
            return Result.Failure(
                ErrorCodes.MakerCheckerUnknownOperation,
                $"No executor registered for operation '{row.Operation}'.");
        }
        return await executor.ExecuteAsync(row.Operation, row.PayloadJson, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> RejectAsync(string actionSqid, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Rejection reason is required.");
        }

        var loaded = await LoadAndGuardAsync(actionSqid, ct).ConfigureAwait(false);
        if (loaded.Failure is { } failure) return failure;
        var row = loaded.Row!;

        // Maker ≠ checker holds for rejection too — a maker withdrawing their own
        // action would defeat the 4-eyes ceremony's accountability trail. The proper
        // surface for "I changed my mind" is a separate cancel endpoint (future work).
        if (_caller.UserId is long checkerId && checkerId == row.MakerUserId)
        {
            return Result.Failure(
                ErrorCodes.MakerCheckerSelfApprovalForbidden,
                "Maker cannot reject their own action.");
        }
        if (_caller.UserId is not long rejecterId)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var now = _clock.UtcNow;
        row.Status = PendingAdminActionStatus.Rejected;
        row.CheckerUserId = rejecterId;
        row.CheckerDecidedAtUtc = now;
        row.RejectionReason = reason;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        // R0040 — rejected-decision counter; counted AFTER persistence so a SaveChanges
        // throw doesn't double-count when the caller retries.
        CnasMeter.AdminActionRejected.Add(1);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<PendingAdminActionItem>>> ListPendingAsync(PageRequest page, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!_caller.Roles.Contains(AdminRole))
        {
            return Result<PagedResult<PendingAdminActionItem>>.Failure(
                ErrorCodes.Forbidden, "Caller lacks cnas-admin role.");
        }

        var now = _clock.UtcNow;
        var pageSize = Math.Clamp(page.PageSize, 1, 200);
        var pageNumber = Math.Max(1, page.Page);
        var skip = (pageNumber - 1) * pageSize;

        // Only truly-pending rows show in the queue — expired rows are filtered out
        // even before the sweeper closes them so checkers don't waste a click.
        var query = _db.PendingAdminActions
            .Where(p => p.IsActive
                        && p.Status == PendingAdminActionStatus.Pending
                        && p.ExpiresAtUtc > now)
            .OrderBy(p => p.MakerRequestedAtUtc);

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);

        // Sqid encoding happens in-memory after the SQL round-trip (ISqidService is
        // not translatable to SQL) — same pattern as UserAdministrationService.ListAsync.
        var raw = await query
            .Skip(skip).Take(pageSize)
            .Select(p => new { p.Id, p.Operation, p.MakerUserId, p.MakerRequestedAtUtc, p.ExpiresAtUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        var items = raw
            .Select(p => new PendingAdminActionItem(
                _sqids.Encode(p.Id),
                p.Operation,
                _sqids.Encode(p.MakerUserId),
                p.MakerRequestedAtUtc,
                p.ExpiresAtUtc))
            .ToList();

        return Result<PagedResult<PendingAdminActionItem>>.Success(
            new PagedResult<PendingAdminActionItem>(items, pageNumber, pageSize, total));
    }

    /// <summary>
    /// Resolves <paramref name="actionSqid"/> to the underlying row and enforces the
    /// common pre-decision guards: admin role, valid sqid, row exists + active,
    /// status is <see cref="PendingAdminActionStatus.Pending"/>, and TTL has not
    /// elapsed. The TTL guard is destructive — when it fires it flips the row to
    /// <see cref="PendingAdminActionStatus.Expired"/> and persists the change so the
    /// queue surface stays consistent.
    /// </summary>
    /// <param name="actionSqid">Sqid-encoded id supplied by the caller.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A loaded row, or a structured failure to propagate to the caller.</returns>
    private async Task<(PendingAdminAction? Row, Result? Failure)> LoadAndGuardAsync(string actionSqid, CancellationToken ct)
    {
        if (!_caller.Roles.Contains(AdminRole))
        {
            return (null, Result.Failure(ErrorCodes.Forbidden, "Caller lacks cnas-admin role."));
        }

        var decoded = _sqids.TryDecode(actionSqid);
        if (decoded.IsFailure)
        {
            return (null, Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!));
        }

        var row = await _db.PendingAdminActions
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return (null, Result.Failure(ErrorCodes.NotFound, "Pending admin action not found."));
        }

        // Already approved / rejected / expired — idempotent guard so a duplicate UI
        // click cannot double-execute the action.
        if (row.Status != PendingAdminActionStatus.Pending)
        {
            return (null, Result.Failure(
                ErrorCodes.MakerCheckerAlreadyDecided,
                $"Pending admin action already in status {row.Status}."));
        }

        // TTL guard — destructive (flip + save) so the queue self-heals even without
        // the background sweeper. Audit traceability: the expiry timestamp is the
        // clock's "now", recorded via UpdatedAtUtc.
        var now = _clock.UtcNow;
        if (row.ExpiresAtUtc <= now)
        {
            row.Status = PendingAdminActionStatus.Expired;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = _caller.UserSqid;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            // R0040 — inline-expiry counter; one increment per row newly flipped to
            // Expired by the approve-path TTL guard. The background sweeper increments
            // the same counter for the rows IT flips (see MakerCheckerExpirySweeper).
            CnasMeter.AdminActionExpired.Add(1);
            return (null, Result.Failure(
                ErrorCodes.MakerCheckerExpired,
                "Pending admin action expired before approval."));
        }

        return (row, null);
    }
}

/// <summary>
/// Placeholder executor that handles the <c>DEMO.NOOP</c> operation. Acts as the
/// worked-example wired through the 4-eyes pipeline so the integration tests have an
/// end-to-end path to exercise — no destructive admin action exists yet to retrofit
/// (the existing <c>UserAdministrationService.LockAsync</c> / <c>UnlockAsync</c> live
/// behind their own controller surface and would require a deeper refactor; doing
/// that here would couple this batch to R0059 work).
/// </summary>
/// <remarks>
/// TODO[r0058-retrofit]: replace with a real destructive executor (USER.SUSPEND,
/// ROLE.REVOKE, etc.) once the per-action retrofit batch lands. The contract surfaces
/// in this file are deliberately stable so adding a new executor is a single-class
/// addition and a DI registration.
/// </remarks>
public sealed class NoOpDemoExecutor : IPendingAdminActionExecutor
{
    /// <summary>Stable code recognised by this executor.</summary>
    public const string OperationCode = "DEMO.NOOP";

    /// <inheritdoc />
    public bool Handles(string operation) =>
        string.Equals(operation, OperationCode, StringComparison.Ordinal);

    /// <inheritdoc />
    public Task<Result> ExecuteAsync(string operation, string payloadJson, CancellationToken ct = default)
    {
        // Intentionally inert — the executor exists so the controller / service / DI
        // path has an end-to-end smoke surface. Real executors will perform the
        // actual side effect here.
        _ = operation;
        _ = payloadJson;
        _ = ct;
        return Task.FromResult(Result.Success());
    }
}
