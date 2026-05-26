using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services.ApplicationProcessing;

/// <summary>
/// R0570 / TOR CF 08.02 — round-robin implementation of
/// <see cref="IExaminerAssignmentService"/>. Selects the next examiner for
/// a freshly-submitted application using a singleton-row cursor
/// (<see cref="ExaminerAssignmentCursor"/>) for uniform spread across
/// process restarts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Eligible pool.</b> The pool is materialised from <c>UserProfiles</c>
/// where (a) <c>IsActive</c> is true, (b) <see cref="UserProfile.State"/>
/// equals <see cref="UserAccountState.Active"/>, and (c) the user's
/// <see cref="UserProfile.Roles"/> collection contains the literal
/// <c>"cnas-examiner"</c>. The registrar id passed by the caller is then
/// removed from the pool — CF 08.02 forbids the same person registering
/// AND examining a cerere.
/// </para>
/// <para>
/// <b>Selection.</b> The pool is ordered by ascending <c>Id</c> to give a
/// stable canonical sequence. The cursor's
/// <see cref="ExaminerAssignmentCursor.NextIndex"/> is consumed modulo the
/// pool size; the cursor is then incremented and persisted. On modulo wrap
/// the rotation naturally restarts from the first eligible examiner.
/// </para>
/// <para>
/// <b>Empty pool.</b> When the pool is empty after the registrar exclusion
/// the service returns a
/// <see cref="ErrorCodes.ApplicationNoAvailableExaminer"/> failure WITHOUT
/// touching the cursor — a future submission with a different registrar
/// must not skip a slot because of an earlier rejected attempt.
/// </para>
/// <para>
/// <b>Concurrency.</b> The cursor row carries the <see cref="AuditableEntity.Xmin"/>
/// optimistic-concurrency token inherited from
/// <see cref="AuditableEntity"/>. Two parallel submissions that read the
/// same <see cref="ExaminerAssignmentCursor.NextIndex"/> collide on
/// <c>SaveChanges</c>; this service does NOT add an explicit retry loop —
/// the calling pipeline (<c>ApplicationServiceImpl.SubmitAsync</c>) holds
/// the only SaveChanges scope and the EF Core change tracker batches both
/// the cerere insert and the cursor increment into one transaction.
/// </para>
/// </remarks>
public sealed class RoundRobinExaminerAssignmentService : IExaminerAssignmentService
{
    /// <summary>Stable singleton-row key.</summary>
    private const string CursorKey = "default";

    /// <summary>The role claim that marks a user as eligible to examine cereri.</summary>
    private const string ExaminerRole = "cnas-examiner";

    /// <summary>EF Core write-side context — used to read + increment the cursor row.</summary>
    private readonly ICnasDbContext _db;

    /// <summary>UTC clock — stamps the cursor row's UpdatedAtUtc on every successful pick.</summary>
    private readonly ICnasTimeProvider _clock;

    /// <summary>
    /// Constructs the service with its two dependencies.
    /// </summary>
    /// <param name="db">Write-side EF Core context.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    public RoundRobinExaminerAssignmentService(ICnasDbContext db, ICnasTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Maximum number of cursor read+increment+persist attempts before the
    /// service surfaces a <see cref="ErrorCodes.Conflict"/> failure to the
    /// caller. Two retries cover the realistic "two concurrent submissions
    /// land in the same xmin window" case; a third retry catches a pathological
    /// burst, and a fourth would indicate a structural problem rather than a
    /// transient race.
    /// </summary>
    private const int CursorCasMaxAttempts = 3;

    /// <inheritdoc />
    public async Task<Result<long>> AssignExaminerAsync(
        long applicationId,
        long registrarUserId,
        CancellationToken cancellationToken = default)
    {
        // Materialise the eligible pool deterministically (Id ASC). We pull the
        // full pool because EF Core's translation of a contains-on-collection
        // predicate against UserProfile.Roles (Postgres jsonb) needs a server-
        // side function that is not portable to the InMemory test provider —
        // a small client-side filter on a tiny set is well within budget.
        var candidates = await _db.UserProfiles
            .Where(u => u.IsActive && u.State == UserAccountState.Active)
            .OrderBy(u => u.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Apply the role + registrar filters in memory. The pool is bounded by
        // the number of examiner staff (typically a few dozen rows) so the
        // O(N) sweep is negligible compared to the database round-trip.
        var pool = candidates
            .Where(u => u.Roles != null && u.Roles.Contains(ExaminerRole))
            .Where(u => u.Id != registrarUserId)
            .ToList();

        if (pool.Count == 0)
        {
            return Result<long>.Failure(
                ErrorCodes.ApplicationNoAvailableExaminer,
                $"No eligible examiner remains for application {applicationId} "
                    + $"after excluding registrar user {registrarUserId}.");
        }

        // Bounded retry loop on the cursor xmin token. Two concurrent
        // submissions can both observe the same cursor row, increment it, and
        // race on SaveChangesAsync — the loser sees DbUpdateConcurrencyException.
        // The fix re-reads the cursor from a clean change tracker, recomputes
        // the pick against the now-advanced cursor, and re-attempts the save.
        // Bounded at <see cref="CursorCasMaxAttempts"/> so a pathological storm
        // surfaces as Conflict rather than spinning indefinitely.
        for (var attempt = 0; attempt < CursorCasMaxAttempts; attempt++)
        {
            // Read or seed the cursor row. The first call in a fresh environment
            // hits the seed branch — we persist the seeded row right away so the
            // next call observes the same singleton.
            var cursor = await _db.ExaminerAssignmentCursors
                .SingleOrDefaultAsync(c => c.Key == CursorKey, cancellationToken)
                .ConfigureAwait(false);
            if (cursor is null)
            {
                cursor = new ExaminerAssignmentCursor
                {
                    Key = CursorKey,
                    NextIndex = 0,
                    CreatedAtUtc = _clock.UtcNow,
                    IsActive = true,
                };
                _db.ExaminerAssignmentCursors.Add(cursor);
            }

            // Pick the examiner — modulo wraps when the pool size shrinks below
            // the stored index, which can happen after staff churn.
            var pickIndex = (int)(cursor.NextIndex % pool.Count);
            if (pickIndex < 0)
            {
                // Defense in depth: a negative long-mod-positive on .NET can yield a
                // negative result for negative dividends. The cursor is always
                // non-negative in normal operation, but if a deployment back-fills
                // a negative value we still want a deterministic, in-range pick.
                pickIndex += pool.Count;
            }
            var chosen = pool[pickIndex];

            // Advance the cursor BEFORE persisting so a SaveChanges failure does not
            // double-issue the same examiner — the cerere insert is the same
            // SaveChanges scope at the call site, and a single rollback consistently
            // un-picks both rows.
            cursor.NextIndex += 1;
            cursor.UpdatedAtUtc = _clock.UtcNow;

            try
            {
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return Result<long>.Success(chosen.Id);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Drop the local snapshot so the next iteration re-reads the
                // cursor freshly from the DB. Otherwise EF would carry the
                // stale entity into the next SaveChanges and trip the same
                // xmin check again.
                if (_db is DbContext concrete)
                {
                    concrete.ChangeTracker.Clear();
                }
                if (attempt + 1 >= CursorCasMaxAttempts)
                {
                    // Surface a structured Conflict instead of letting the
                    // exception bubble — the caller can decide to surface
                    // a 409 to the API client or retry at a higher level.
                    return Result<long>.Failure(
                        ErrorCodes.Conflict,
                        "Examiner-assignment cursor contention; retry the submission.");
                }
            }
        }

        // Unreachable — the for-loop either returns inside try (success) or
        // returns inside the final catch (Conflict). Defensive return so the
        // compiler is happy and any future refactor that drops the catch's
        // final-attempt return still surfaces a structured failure.
        return Result<long>.Failure(
            ErrorCodes.Conflict,
            "Examiner-assignment cursor contention; retry the submission.");
    }
}
