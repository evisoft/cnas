using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Contributors;

/// <summary>
/// R0302 / TOR §2.1 — façade over the <c>ContributorSourceChangeHistory</c>
/// append-only ledger. Writers (e.g. <c>IContributorService</c>, the MConnect
/// RSUD sync job, manual operator updates) call
/// <see cref="RecordChangeAsync"/> on every <c>SourceSystem</c> mutation; ops
/// dashboards and the contributor detail screen read the per-contributor
/// timeline via <see cref="GetHistoryAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audit + metric emission.</b> Every successful <see cref="RecordChangeAsync"/>
/// call writes a Notice-severity <c>CONTRIBUTOR.SOURCE_CHANGED</c> audit row and
/// increments the <c>cnas.contributor.source_change.recorded</c> counter (tagged
/// with <c>new_source</c>). The history row insertion + audit + metric are
/// performed atomically from the caller's point of view.
/// </para>
/// </remarks>
public interface IContributorSourceHistoryService
{
    /// <summary>Stable audit event code emitted on a successful change record.</summary>
    public const string AuditSourceChanged = "CONTRIBUTOR.SOURCE_CHANGED";

    /// <summary>
    /// Persists one history row capturing a contributor's source-system flip.
    /// Idempotency is the writer's responsibility — calling this multiple times
    /// with the same arguments simply appends multiple rows.
    /// </summary>
    /// <param name="contributorId">
    /// Internal primary key of the parent contributor. Internal int/long per
    /// CLAUDE.md RULE 3 (only DTOs carry Sqids).
    /// </param>
    /// <param name="oldSource">
    /// Prior source-system value. <c>null</c> only on the FIRST history row for
    /// a contributor (initial registration).
    /// </param>
    /// <param name="newSource">
    /// New source-system value. Required; ≤ 64 chars; non-empty.
    /// </param>
    /// <param name="actorUserId">
    /// FK to the <see cref="Cnas.Ps.Core.Domain.UserProfile"/> primary id of the
    /// operator that recorded the change, or <c>null</c> for system writers.
    /// </param>
    /// <param name="reason">
    /// Free-form operator-supplied justification (≤ 500 chars); optional.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> on success;
    /// <see cref="ErrorCodes.ValidationFailed"/> when <paramref name="newSource"/>
    /// is empty / oversize.
    /// </returns>
    Task<Result> RecordChangeAsync(
        long contributorId,
        string? oldSource,
        string newSource,
        long? actorUserId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a page of history rows for a contributor, ordered
    /// <c>ChangedAtUtc DESC</c>.
    /// </summary>
    /// <param name="contributorSqid">Sqid-encoded contributor id.</param>
    /// <param name="skip">0-based offset (≥ 0).</param>
    /// <param name="take">Page size, clamped to <c>1..200</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> with the page;
    /// <see cref="ErrorCodes.InvalidSqid"/> when the Sqid does not decode;
    /// <see cref="ErrorCodes.NotFound"/> when no contributor matches.
    /// </returns>
    Task<Result<ContributorSourceChangeHistoryPageDto>> GetHistoryAsync(
        string contributorSqid,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
