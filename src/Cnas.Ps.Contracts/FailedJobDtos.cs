namespace Cnas.Ps.Contracts;

/// <summary>
/// External-facing projection of a <c>FailedJob</c> row — one entry in the dead-letter
/// queue produced by <c>FailedJobListener</c> when a Quartz job execution ends in
/// failure (CLAUDE.md §6.2).
/// </summary>
/// <remarks>
/// The <see cref="Id"/> is Sqid-encoded per CLAUDE.md RULE 3 so that operators looking
/// at the admin dashboard cannot infer "how many jobs have failed since launch" from
/// monotonic primary keys. The <see cref="JobName"/> identifies the source job — it is
/// not encoded because the job key (e.g. <c>mpay-dispatcher</c>) is itself a public
/// identifier baked into <c>QuartzComposition</c>.
/// </remarks>
/// <param name="Id">Sqid-encoded id of the DLQ entry. Use in the replay endpoint URL.</param>
/// <param name="JobName">Quartz job name (e.g. <c>mpay-dispatcher</c>).</param>
/// <param name="JobGroup">Quartz job group, usually <c>DEFAULT</c>.</param>
/// <param name="FailedAtUtc">UTC instant at which the failure was recorded.</param>
/// <param name="ExceptionType">Exception type FQN (e.g. <c>System.Net.Http.HttpRequestException</c>).</param>
/// <param name="ExceptionMessage">Exception message — already truncated to 4000 chars by the listener.</param>
/// <param name="StackTrace">
/// Full stack trace — already truncated to 16000 chars by the listener. May be
/// <c>null</c> when the exception did not carry a stack (rare, but possible for
/// synthetic <see cref="System.Exception"/> instances created without throw).
/// </param>
/// <param name="RefireCount">Quartz refire count at the moment of failure.</param>
/// <param name="ReplayState">
/// Replay status: <c>null</c> when never replayed; otherwise the textual outcome
/// of the most recent replay attempt (e.g. <c>"scheduled"</c>).
/// </param>
/// <param name="LastReplayAtUtc">UTC instant of the most recent admin replay, if any.</param>
public sealed record FailedJobOutput(
    string Id,
    string JobName,
    string JobGroup,
    DateTime FailedAtUtc,
    string ExceptionType,
    string ExceptionMessage,
    string? StackTrace,
    int RefireCount,
    string? ReplayState,
    DateTime? LastReplayAtUtc);
