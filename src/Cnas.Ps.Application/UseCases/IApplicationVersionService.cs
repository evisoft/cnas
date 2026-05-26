using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0321 / R0224 / UI 008 — server-side persistence surface for the autosave / draft
/// version-history feature. Every call to <see cref="SaveAsync"/> captures a new
/// <see cref="ApplicationVersion"/> snapshot (subject to the dedup guard) bound to a
/// <see cref="ServiceApplication"/>; <see cref="RevertAsync"/> restores a prior
/// snapshot by saving its <c>FormDataJson</c> as a fresh version with source
/// <see cref="ApplicationVersionSource.Revert"/>; <see cref="ListAsync"/> and
/// <see cref="GetAsync"/> surface the version history to the citizen-facing UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authorisation.</b> Every operation enforces an ownership / management-role
/// check at the service layer: the caller must either own the parent application
/// (<see cref="ServiceApplication.SolicitantId"/> == caller user id) OR hold one of
/// the management roles (<c>cnas-decider</c>, <c>cnas-admin</c>, <c>cnas-tech-admin</c>).
/// Foreign callers receive <see cref="ErrorCodes.Forbidden"/>.
/// </para>
/// <para>
/// <b>Editability gate.</b> Saves are only accepted while the application is in
/// <see cref="ApplicationStatus.Draft"/>; the
/// <see cref="ApplicationVersionSource.Submit"/> source is additionally allowed on
/// <see cref="ApplicationStatus.Submitted"/> so the submission ceremony can capture
/// its own final snapshot. Any other status returns
/// <see cref="ErrorCodes.ApplicationNotEditable"/>.
/// </para>
/// <para>
/// <b>Dedup.</b> When the supplied <c>FormDataJson</c> byte-matches the current
/// version's payload, no new row is written and the existing current version is
/// returned via <see cref="Result{T}.Success"/>. This keeps the autosave tick cheap
/// on no-op pages.
/// </para>
/// <para>
/// <b>Autosave cap.</b> Implementations enforce a per-application cap on
/// <see cref="ApplicationVersionSource.Autosave"/> rows (default 50) — the oldest
/// autosave row is HARD-DELETED when the cap is exceeded. Manual saves, submits, and
/// reverts are NEVER pruned.
/// </para>
/// <para>
/// <b>Sqid boundary.</b> Every id on the surface is the Sqid string form per
/// CLAUDE.md RULE 3. Raw <see cref="long"/> primary keys never appear here.
/// </para>
/// </remarks>
public interface IApplicationVersionService
{
    /// <summary>
    /// Captures a new version snapshot of the supplied application. Dedup short-circuits
    /// when the payload matches the current version; cap enforcement prunes the oldest
    /// autosave row when the per-application autosave cap is exceeded.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the owning application.</param>
    /// <param name="formDataJson">Form payload to snapshot.</param>
    /// <param name="source">Origin of the save (autosave tick, manual click, submit ceremony, revert action).</param>
    /// <param name="note">Optional free-form annotation (≤ 1000 chars).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On success the persisted (or dedup-returned) version row.
    /// <see cref="ErrorCodes.InvalidSqid"/> when the Sqid does not decode.
    /// <see cref="ErrorCodes.NotFound"/> when the application does not exist.
    /// <see cref="ErrorCodes.Forbidden"/> when the caller is not the owner / manager.
    /// <see cref="ErrorCodes.Unauthorized"/> when the caller is anonymous.
    /// <see cref="ErrorCodes.ApplicationNotEditable"/> when the application is in a terminal status.
    /// </returns>
    Task<Result<ApplicationVersionOutputDto>> SaveAsync(
        string applicationSqid,
        string formDataJson,
        ApplicationVersionSource source,
        string? note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a prior version by saving its <c>FormDataJson</c> as a fresh row with
    /// source <see cref="ApplicationVersionSource.Revert"/>. The target version remains
    /// in the history untouched; the new row becomes the current version.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the owning application.</param>
    /// <param name="targetVersionNumber">Version number to restore (must already exist).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// On success the newly-written revert row (NOT the target row).
    /// <see cref="ErrorCodes.NotFound"/> when the target version does not exist.
    /// <see cref="ErrorCodes.Forbidden"/> when the caller is not the owner / manager.
    /// <see cref="ErrorCodes.ApplicationNotEditable"/> when the application is terminal.
    /// </returns>
    Task<Result<ApplicationVersionOutputDto>> RevertAsync(
        string applicationSqid,
        int targetVersionNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every version row for the supplied application, most recent first
    /// (<see cref="ApplicationVersion.VersionNumber"/> DESC). The <c>FormDataJson</c>
    /// payload is omitted from each row to keep the response small — fetch a single
    /// version via <see cref="GetAsync"/> when the payload is needed.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the owning application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ordered list, or a failure when the application is not accessible.</returns>
    Task<Result<IReadOnlyList<ApplicationVersionSummaryDto>>> ListAsync(
        string applicationSqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a single version by application + version number, including the
    /// <c>FormDataJson</c> payload.
    /// </summary>
    /// <param name="applicationSqid">Sqid-encoded id of the owning application.</param>
    /// <param name="versionNumber">Version number to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version row, or a structured failure (NotFound / Forbidden / InvalidSqid).</returns>
    Task<Result<ApplicationVersionOutputDto>> GetAsync(
        string applicationSqid,
        int versionNumber,
        CancellationToken cancellationToken = default);
}
