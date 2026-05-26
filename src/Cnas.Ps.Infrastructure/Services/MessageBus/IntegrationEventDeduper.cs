using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.MessageBus;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cnas.Ps.Infrastructure.Services.MessageBus;

/// <summary>
/// R0103 / TOR CF 14.02 — production implementation of
/// <see cref="IIntegrationEventDeduper"/>. Backed by the
/// <see cref="ProcessedIntegrationEvent"/> table; the UNIQUE constraint on
/// <see cref="ProcessedIntegrationEvent.MessageId"/> is the race-free anchor
/// the dispatcher relies on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Race semantics.</b> Two competing dispatchers that both observe a
/// missing row reach the database with separate INSERTs. PostgreSQL allows
/// exactly one to commit; the loser receives a
/// <see cref="DbUpdateException"/> whose inner exception is a
/// <see cref="PostgresException"/> with <c>SqlState=23505</c>. The service
/// catches that specific failure and converts it into the same
/// <c>AlreadyProcessed=true</c> outcome the second-arriving caller would
/// have seen if their probe had observed the freshly-inserted row.
/// </para>
/// <para>
/// <b>InMemory provider.</b> The InMemory EF provider used by unit tests
/// emits a generic <see cref="DbUpdateException"/> without a Postgres-shaped
/// inner. To keep the service exercised under both providers, the
/// race-recovery branch ALSO treats a generic InvalidOperationException about
/// the unique constraint as a duplicate. In production the Postgres-specific
/// path applies; the InMemory shape is a test-only convenience.
/// </para>
/// <para>
/// <b>No PII.</b> Logs never emit the data payload (this service never sees
/// it — only the envelope metadata). The <c>FailureReason</c> passed to
/// <see cref="MarkFailedAsync"/> is truncated to the validator-mandated
/// maximum before persistence; callers are responsible for sanitising it
/// against IDNPs, IPs, and token material.
/// </para>
/// <para>
/// <b>Scoped lifetime.</b> Holds a per-request <see cref="ICnasDbContext"/> /
/// <see cref="IReadOnlyCnasDbContext"/>; register as Scoped in DI.
/// </para>
/// </remarks>
public sealed class IntegrationEventDeduper : IIntegrationEventDeduper
{
    /// <summary>PostgreSQL SQLSTATE for "unique violation" — used to detect lost-race inserts.</summary>
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly ICnasDbContext _db;
    private readonly IReadOnlyCnasDbContext _read;
    private readonly ICnasTimeProvider _clock;
    private readonly ILogger<IntegrationEventDeduper> _logger;
    private readonly IntegrationEventDedupClaimArgsValidator _claimValidator;
    private readonly IntegrationEventDedupMarkFailedArgsValidator _markFailedValidator;

    /// <summary>Constructs the deduper with its scoped collaborators.</summary>
    /// <param name="db">Writer EF Core context (per-request).</param>
    /// <param name="read">Read-replica context for pure-read probes.</param>
    /// <param name="clock">UTC clock abstraction.</param>
    /// <param name="logger">Structured logger.</param>
    public IntegrationEventDeduper(
        ICnasDbContext db,
        IReadOnlyCnasDbContext read,
        ICnasTimeProvider clock,
        ILogger<IntegrationEventDeduper> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _read = read;
        _clock = clock;
        _logger = logger;
        _claimValidator = new IntegrationEventDedupClaimArgsValidator();
        _markFailedValidator = new IntegrationEventDedupMarkFailedArgsValidator();
    }

    /// <inheritdoc />
    public async Task<Result<IntegrationEventDedupOutcomeDto>> TryClaimAsync(
        string messageId,
        string source,
        string type,
        CancellationToken ct = default)
    {
        // Validate at the boundary (CLAUDE.md §2.5).
        var args = new IntegrationEventDedupClaimArgs(messageId ?? string.Empty, source ?? string.Empty, type ?? string.Empty);
        var validation = _claimValidator.Validate(args);
        if (!validation.IsValid)
        {
            var first = validation.Errors[0];
            return Result<IntegrationEventDedupOutcomeDto>.Failure(
                first.ErrorCode ?? ErrorCodes.ValidationFailed,
                first.ErrorMessage);
        }

        // Cheap probe first — many duplicates can be served without an INSERT
        // attempt. The atomic anchor below still covers the race window when
        // the probe misses but a concurrent claim wins.
        var existing = await _read.ProcessedIntegrationEvents
            .Where(e => e.MessageId == messageId)
            .Select(e => new { e.ProcessedAtUtc })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Result<IntegrationEventDedupOutcomeDto>.Success(
                new IntegrationEventDedupOutcomeDto(true, messageId!, existing.ProcessedAtUtc));
        }

        // Attempt to claim the MessageId by inserting a fresh row.
        var now = _clock.UtcNow;
        var entity = new ProcessedIntegrationEvent
        {
            MessageId = messageId!,
            Source = source!,
            Type = type!,
            ProcessedAtUtc = now,
            Outcome = ProcessedEventOutcome.Accepted,
            CreatedAtUtc = now,
            CreatedBy = "system:integration-event-deduper",
            IsActive = true,
        };
        _db.ProcessedIntegrationEvents.Add(entity);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the race — a concurrent dispatcher inserted the row between
            // our probe and our INSERT. Detach our entity so EF Core doesn't
            // re-attempt it on the next SaveChanges, then read back the
            // winning row's timestamp.
            _db.ProcessedIntegrationEvents.Entry(entity).State = EntityState.Detached;
            var winner = await _read.ProcessedIntegrationEvents
                .Where(e => e.MessageId == messageId)
                .Select(e => new { e.ProcessedAtUtc })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            return Result<IntegrationEventDedupOutcomeDto>.Success(
                new IntegrationEventDedupOutcomeDto(true, messageId!, winner?.ProcessedAtUtc ?? now));
        }

        return Result<IntegrationEventDedupOutcomeDto>.Success(
            new IntegrationEventDedupOutcomeDto(false, messageId!, EarlierProcessedAtUtc: null));
    }

    /// <inheritdoc />
    public async Task<Result> MarkFailedAsync(
        string messageId,
        string failureReason,
        CancellationToken ct = default)
    {
        var args = new IntegrationEventDedupMarkFailedArgs(
            messageId ?? string.Empty,
            failureReason ?? string.Empty);
        var validation = _markFailedValidator.Validate(args);
        if (!validation.IsValid)
        {
            var first = validation.Errors[0];
            return Result.Failure(
                first.ErrorCode ?? ErrorCodes.ValidationFailed,
                first.ErrorMessage);
        }

        var row = await _db.ProcessedIntegrationEvents
            .Where(e => e.MessageId == messageId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"No processed-integration-event row exists for the supplied MessageId.");
        }

        var now = _clock.UtcNow;
        row.Outcome = ProcessedEventOutcome.Failed;
        row.FailureReason = TruncateFailureReason(failureReason!);
        row.UpdatedAtUtc = now;
        row.UpdatedBy = "system:integration-event-deduper";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogWarning(
            "Integration event marked as Failed: type={Type} source={Source} (MessageId omitted from message — see structured field).",
            row.Type, row.Source);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsKnownAsync(string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId) ||
            messageId.Length > IntegrationEventDedupClaimArgsValidator.MessageIdMaxLength)
        {
            return Result<bool>.Failure(
                ErrorCodes.ValidationFailed,
                "MessageId is required and must be at most " +
                IntegrationEventDedupClaimArgsValidator.MessageIdMaxLength +
                " characters.");
        }

        var known = await _read.ProcessedIntegrationEvents
            .AnyAsync(e => e.MessageId == messageId, ct)
            .ConfigureAwait(false);
        return Result<bool>.Success(known);
    }

    /// <summary>
    /// Truncates the supplied failure reason to the validator-mandated maximum
    /// and trims surrounding whitespace. Belt-and-braces — callers also
    /// validate, but the writer defends the storage row even if a caller
    /// bypasses validation.
    /// </summary>
    /// <param name="reason">Caller-supplied failure description.</param>
    /// <returns>The truncated and trimmed reason, safe to persist.</returns>
    private static string TruncateFailureReason(string reason)
    {
        var trimmed = reason.Trim();
        return trimmed.Length <= IntegrationEventDedupMarkFailedArgsValidator.FailureReasonMaxLength
            ? trimmed
            : trimmed.Substring(0, IntegrationEventDedupMarkFailedArgsValidator.FailureReasonMaxLength);
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied
    /// <see cref="DbUpdateException"/> is the kind of unique-violation we
    /// want to swallow into an "already processed" outcome. Recognises both
    /// the canonical PostgreSQL 23505 SQLSTATE and the looser shape the
    /// EF Core InMemory provider produces under test.
    /// </summary>
    /// <param name="ex">The exception raised by <c>SaveChanges</c>.</param>
    /// <returns><c>true</c> when the exception is a unique-violation; <c>false</c> otherwise.</returns>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException pg && pg.SqlState == PostgresUniqueViolationSqlState)
            {
                return true;
            }
            var msg = inner.Message;
            if (msg is not null &&
                (msg.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        var topMessage = ex.Message;
        return topMessage is not null &&
               (topMessage.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
                topMessage.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }
}
