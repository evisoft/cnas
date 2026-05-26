namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Dead-letter-queue (DLQ) entry for a Quartz background job whose execution failed
/// definitively — i.e. raised an exception after its in-job Polly retry pipeline was
/// exhausted (CLAUDE.md §6.2 — "monitored, retryable, logged"). Each row is a record
/// of one fire that could not be processed; operators query this table for forensic
/// debugging and may "replay" entries to re-schedule the job with the original
/// <see cref="JobDataJson"/> payload.
/// </summary>
/// <remarks>
/// <para>
/// The DLQ is structurally similar to <see cref="AuditLog"/> — write-only, indexed for
/// query, and PII-scrubbed before persistence (the Quartz <c>JobDataMap</c> can carry
/// arbitrary keys, so the listener that produces these rows redacts keys whose names
/// hint at sensitive content; see <c>FailedJobListener</c> for the scrub-list).
/// </para>
/// <para>
/// Replay semantics: an admin POSTing to the replay endpoint causes the
/// <see cref="ReplayState"/> column to transition from <c>null</c> to
/// <c>"scheduled"</c>, with <see cref="LastReplayAtUtc"/> stamped. The replay schedules
/// a fresh one-shot Quartz fire using the original job key and data; the new fire is
/// independent of this DLQ row and, if it fails again, produces a fresh DLQ entry.
/// </para>
/// </remarks>
public sealed class FailedJob : AuditableEntity, IExternalId
{
    /// <summary>Quartz <c>JobKey.Name</c> — the canonical job identifier (e.g. <c>mpay-dispatcher</c>).</summary>
    public required string JobName { get; set; }

    /// <summary>Quartz <c>JobKey.Group</c> — usually <c>"DEFAULT"</c> but kept for safety so the replay can re-target the exact job.</summary>
    public required string JobGroup { get; set; }

    /// <summary>UTC instant at which the FAILED attempt finished (not when retries started).</summary>
    public DateTime FailedAtUtc { get; set; }

    /// <summary>Exception type fully-qualified name (e.g. <c>System.Net.Http.HttpRequestException</c>).</summary>
    public required string ExceptionType { get; set; }

    /// <summary>Exception message — truncated to 4000 chars for storage safety.</summary>
    public required string ExceptionMessage { get; set; }

    /// <summary>Full stack trace — truncated to 16000 chars (~250 lines) to bound row size.</summary>
    public string? StackTrace { get; set; }

    /// <summary>
    /// Serialized <c>JobDataMap</c> as JSON. PII MUST be scrubbed before persistence — see
    /// <c>FailedJobListener</c> for the redaction policy (any key containing
    /// <c>idnp</c>, <c>pin</c>, <c>password</c>, <c>token</c>, <c>secret</c>, or
    /// <c>key</c> case-insensitively is replaced with <c>"&lt;redacted&gt;"</c>).
    /// </summary>
    public string? JobDataJson { get; set; }

    /// <summary>
    /// Refire count Quartz had reached when the failure was recorded. Useful for operators
    /// distinguishing "failed on first attempt" from "failed after N misfires".
    /// </summary>
    public int RefireCount { get; set; }

    /// <summary>
    /// Replay state — <c>null</c> when never replayed; non-null carries the last replay
    /// attempt outcome (e.g. <c>"scheduled"</c>, <c>"failed"</c>). Stored as a short string
    /// rather than an enum so future replay outcomes can be added without a migration.
    /// </summary>
    public string? ReplayState { get; set; }

    /// <summary>UTC instant at which an admin last replayed this job; <c>null</c> when never replayed.</summary>
    public DateTime? LastReplayAtUtc { get; set; }
}
