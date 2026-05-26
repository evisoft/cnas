using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Integrity;

/// <summary>
/// R2282 / TOR SEC 036 — service façade over the row-integrity check
/// subsystem. Hosts the manual-trigger entrypoint, the per-run lookups, and
/// the finding-acknowledgement path. The scheduled Quartz job invokes this
/// service indirectly through the same check pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit attribution.</b> Each interesting transition is captured by a
/// stable audit code:
/// <list type="bullet">
///   <item><see cref="StartManualRunAsync"/> → <c>INTEGRITY_CHECK.MANUAL_RUN_STARTED</c> at Critical severity.</item>
///   <item><see cref="AcknowledgeFindingAsync"/> → <c>INTEGRITY_CHECK.FINDING_ACKNOWLEDGED</c> at Critical severity.</item>
/// </list>
/// </para>
/// </remarks>
public interface IIntegrityCheckService
{
    /// <summary>
    /// Triggers a fresh integrity-check run synchronously (in-band — the
    /// caller awaits completion). The service executes every registered
    /// <see cref="IIntegrityCheck"/>, persists the findings, and returns the
    /// completed run summary. Emits the <c>INTEGRITY_CHECK.MANUAL_RUN_STARTED</c>
    /// Critical audit row.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The completed <see cref="IntegrityCheckRunDto"/> on success.</returns>
    Task<Result<IntegrityCheckRunDto>> StartManualRunAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single run by its Sqid (no findings attached). Returns
    /// <see cref="ErrorCodes.InvalidSqid"/> on a malformed input or
    /// <see cref="ErrorCodes.NotFound"/> on a missing row.
    /// </summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The run summary on success.</returns>
    Task<Result<IntegrityCheckRunDto>> GetRunByIdAsync(string sqid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single run together with its findings list. Findings are
    /// returned in <c>FirstDetectedAt ASC</c> order (insertion order).
    /// </summary>
    /// <param name="sqid">Sqid-encoded run id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The run-with-findings envelope on success.</returns>
    Task<Result<IntegrityCheckRunDetailsDto>> GetRunDetailsAsync(string sqid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the most-recent integrity-check runs, ordered by
    /// <c>RunStartedAt DESC</c>. The take is clamped to the 1..100 inclusive
    /// range; values outside that range return
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </summary>
    /// <param name="take">Number of runs to return (1..100).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>An ordered list — empty when no runs exist.</returns>
    Task<Result<IReadOnlyList<IntegrityCheckRunDto>>> ListRecentRunsAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges a finding. Stamps the user, note + timestamp on the row
    /// and emits the <c>INTEGRITY_CHECK.FINDING_ACKNOWLEDGED</c> Critical
    /// audit row.
    /// </summary>
    /// <param name="findingSqid">Sqid-encoded finding id.</param>
    /// <param name="input">Acknowledgement payload (note, 3..1000 chars).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<IntegrityCheckFindingDto>> AcknowledgeFindingAsync(
        string findingSqid,
        IntegrityFindingAcknowledgeInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists open (un-acknowledged) findings filtered by severity / aggregate
    /// / check code. The result is paged via <c>Skip/Take</c> and ordered by
    /// <c>FirstDetectedAt DESC</c>.
    /// </summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The matching findings page on success.</returns>
    Task<Result<IntegrityFindingPageDto>> ListOpenFindingsAsync(
        IntegrityFindingFilterDto filter,
        CancellationToken cancellationToken = default);
}
