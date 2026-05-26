using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0116 + R0195 / TOR SEC 054-055 — admin-facing service for the MLog
/// dual-write category filter registry. List / upsert / deactivate the
/// admin-configurable toggles consulted by the audit drainer before each
/// MLog forward.
/// </summary>
public interface IMLogCategoryConfigService
{
    /// <summary>Lists active (and optionally inactive) MLog category rows.</summary>
    /// <param name="includeInactive">When <c>true</c> deactivated rows are included.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows sorted by <c>CategoryCode</c>.</returns>
    Task<Result<IReadOnlyList<MLogCategoryConfigDto>>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new MLog category row or updates the existing row keyed by
    /// <see cref="MLogCategoryConfigInputDto.CategoryCode"/>.
    /// </summary>
    /// <param name="input">Filter payload (validated at the API boundary).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted DTO on success.</returns>
    Task<Result<MLogCategoryConfigDto>> UpsertAsync(
        MLogCategoryConfigInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Deactivates an existing MLog category filter. Idempotent.</summary>
    /// <param name="sqid">Sqid-encoded id of the filter row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success on flip; <see cref="ErrorCodes.NotFound"/> if unknown.</returns>
    Task<Result> DeactivateAsync(
        string sqid,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// R0116 + R0195 — synchronous in-memory snapshot of the active MLog category
/// filter registry. Used by the audit drainer to decide whether to mirror an
/// individual audit event to MLog.
/// </summary>
public interface IMLogCategoryFilter
{
    /// <summary>
    /// Decides whether the supplied audit event should be mirrored to MLog
    /// given the current registry state.
    /// </summary>
    /// <param name="eventCode">Audit event code (e.g. <c>APPLICATION.RECEIVE.SUBMITTED</c>).</param>
    /// <param name="severity">Severity of the audit event.</param>
    /// <returns><c>true</c> when the event should be forwarded, <c>false</c> otherwise.</returns>
    bool ShouldMirror(string eventCode, Cnas.Ps.Core.Domain.AuditSeverity severity);
}
