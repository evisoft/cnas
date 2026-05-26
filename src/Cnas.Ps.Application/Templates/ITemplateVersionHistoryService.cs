using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Templates;

/// <summary>
/// R0132 / CF 17.18 — admin-facing read + rollback surface over the historical
/// versions of a <c>DocumentTemplate</c>. <c>DocumentTemplate</c> already carries
/// <c>Version</c>/<c>IsCurrent</c> columns from R0131; this service exposes the
/// historical retrieval surface that was missing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three operations, no entity-shape changes.</b>
/// <list type="bullet">
///   <item><see cref="ListVersionsAsync"/> — paged listing of every version row for a
///         given template code, ordered Version DESC.</item>
///   <item><see cref="DiffAsync"/> — structured field-level diff between two versions of
///         the same template.</item>
///   <item><see cref="RollbackToAsync"/> — creates a NEW current version copying content
///         from a target older version. The original target row is NEVER mutated.</item>
/// </list>
/// </para>
/// <para>
/// <b>Authorisation.</b> Caller must be authenticated; the REST controller layers the
/// CnasAdmin policy on top.
/// </para>
/// </remarks>
public interface ITemplateVersionHistoryService
{
    /// <summary>
    /// Lists every version row for the supplied <paramref name="templateCode"/>,
    /// ordered <c>Version</c> descending so the current version is first.
    /// </summary>
    /// <param name="templateCode">Stable kebab-case template code (matches <c>DocumentTemplate.Code</c>).</param>
    /// <param name="skip">Zero-based offset.</param>
    /// <param name="take">Page size (1..200).</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the paged list on success;
    /// <see cref="ErrorCodes.NotFound"/> when no row matches the code;
    /// <see cref="ErrorCodes.ValidationFailed"/> on bad paging.
    /// </returns>
    System.Threading.Tasks.Task<Result<TemplateVersionPageDto>> ListVersionsAsync(
        string templateCode,
        int skip,
        int take,
        System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces a structured diff between two versions of the SAME template (both rows
    /// must share the same <c>Code</c>; cross-template diffs are rejected).
    /// </summary>
    /// <param name="baselineVersionSqid">Sqid-encoded id of the baseline (earlier) version row.</param>
    /// <param name="currentVersionSqid">Sqid-encoded id of the current (later) version row.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the diff on success;
    /// <see cref="ErrorCodes.InvalidSqid"/> on malformed sqid;
    /// <see cref="ErrorCodes.NotFound"/> when either row is missing;
    /// <see cref="ErrorCodes.TemplateVersionMismatch"/> when the two rows belong to
    /// different template codes.
    /// </returns>
    System.Threading.Tasks.Task<Result<TemplateVersionDiffDto>> DiffAsync(
        string baselineVersionSqid,
        string currentVersionSqid,
        System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls a template back to a previous <paramref name="targetVersionSqid"/> by
    /// creating a new current version that copies the content of the target. The
    /// original target row stays untouched; the previous "current" row is demoted
    /// (its <c>IsCurrent</c> flag is flipped to <c>false</c>).
    /// </summary>
    /// <param name="targetVersionSqid">Sqid-encoded id of the target (older) version to copy.</param>
    /// <param name="input">Rollback metadata, carrying the required justification reason.</param>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the new current version's
    /// <see cref="DocumentTemplateDto"/> on success;
    /// <see cref="ErrorCodes.InvalidSqid"/> on malformed sqid;
    /// <see cref="ErrorCodes.NotFound"/> when the target row is missing;
    /// <see cref="ErrorCodes.ValidationFailed"/> on bad input.
    /// </returns>
    System.Threading.Tasks.Task<Result<DocumentTemplateDto>> RollbackToAsync(
        string targetVersionSqid,
        TemplateRollbackInputDto input,
        System.Threading.CancellationToken cancellationToken = default);
}
