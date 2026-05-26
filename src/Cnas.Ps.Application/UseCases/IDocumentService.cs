using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// UC11 — Document upload / download / list. Authorisation enforced at the service
/// layer; row-level filtering for the list path goes through the R0671
/// <see cref="Cnas.Ps.Application.AccessScope.IAccessScopeFilter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>List surface.</b> <see cref="ListAsync"/> is the document-registry equivalent of
/// <see cref="ISolicitantService.SearchAsync"/> / <see cref="Audit.IAuditExplorerService.SearchAsync"/>:
/// canonical QBE + budget + access-scope pipeline. Sqid round-tripping happens at the
/// API boundary; the service body deals in raw long ids.
/// </para>
/// <para>
/// <b>Failure modes.</b>
/// <list type="bullet">
///   <item><see cref="ErrorCodes.ValidationFailed"/> — input validator rejected the envelope.</item>
///   <item><see cref="ErrorCodes.QueryTooBroad"/> — budget guard refused; verdict on <see cref="LastBudgetVerdict"/>.</item>
///   <item>Any of the <c>QBE_*</c> family — converter rejected the QBE envelope.</item>
/// </list>
/// </para>
/// </remarks>
public interface IDocumentService
{
    /// <summary>Uploads a new document attachment for the calling user (SEC 010: magic-byte check).</summary>
    /// <param name="fileName">Caller-supplied display name (used as document Title).</param>
    /// <param name="content">Binary payload — read once; must support reset OR be small enough to buffer.</param>
    /// <param name="contentType">Declared MIME type validated against magic bytes.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Sqid-encoded document id on success.</returns>
    Task<Result<string>> UploadAsync(string fileName, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Generates a time-limited presigned URL for the given document, if the caller is authorised.</summary>
    /// <param name="documentId">Sqid-encoded document id.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The presigned URL on success; <see cref="ErrorCodes.NotFound"/> or <see cref="ErrorCodes.Forbidden"/> on failure.</returns>
    Task<Result<Uri>> GetDownloadUrlAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0671 continuation — paged QBE-filterable list of documents in the registry. Wires
    /// the R0163 QBE converter, the R0167 query budget guard, and the R0671 access-scope
    /// filter against the <c>Document</c> registry.
    /// </summary>
    /// <param name="input">Search envelope — optional QBE filter, optional UTC date range, paging.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// On success a <see cref="DocumentsListPageDto"/> carrying Sqid-encoded rows and the
    /// total matching count. On failure one of the codes listed on the interface remarks.
    /// </returns>
    Task<Result<DocumentsListPageDto>> ListAsync(
        DocumentsListInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Most-recent budget verdict captured during a <see cref="ListAsync"/> call. The
    /// controller reads this slot when surfacing a <see cref="ErrorCodes.QueryTooBroad"/>
    /// failure to populate the <c>extensions["budget"]</c> bag on the 422 ProblemDetails.
    /// </summary>
    QueryBudgetVerdict? LastBudgetVerdict { get; }
}
