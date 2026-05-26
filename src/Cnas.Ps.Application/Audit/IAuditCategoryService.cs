using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// R0196 / TOR CF 23.02 — admin-facing registry for audit-category rows.
/// CRUD + Activate / Deactivate lifecycle for the audit-category catalog.
/// Soft-deletes only (the row sticks around with <c>IsActive=false</c> so
/// historical audit rows keep their category target).
/// </summary>
public interface IAuditCategoryService
{
    /// <summary>Stable failure code: a category with the same <c>Code</c> already exists.</summary>
    public const string DuplicateCategoryCodeCode = "AUDIT.CATEGORY.DUPLICATE_CODE";

    /// <summary>Stable failure code: the requested transition is not legal for the current state.</summary>
    public const string InvalidTransitionCode = "AUDIT.CATEGORY.INVALID_TRANSITION";

    /// <summary>Stable audit event code emitted when a category is created.</summary>
    public const string AuditCategoryCreated = "AUDIT.CATEGORY.CREATED";

    /// <summary>Stable audit event code emitted when a category is modified.</summary>
    public const string AuditCategoryModified = "AUDIT.CATEGORY.MODIFIED";

    /// <summary>Stable audit event code emitted when a category transitions state (activate / deactivate).</summary>
    public const string AuditCategoryTransitioned = "AUDIT.CATEGORY.TRANSITIONED";

    /// <summary>Creates a new audit category.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<AuditCategoryDto>> CreateAsync(
        AuditCategoryCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing audit category.</summary>
    /// <param name="categorySqid">Sqid-encoded category id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<AuditCategoryDto>> ModifyAsync(
        string categorySqid,
        AuditCategoryModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Activates an audit category.</summary>
    /// <param name="categorySqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<AuditCategoryDto>> ActivateAsync(
        string categorySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Deactivates an audit category.</summary>
    /// <param name="categorySqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<AuditCategoryDto>> DeactivateAsync(
        string categorySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one category by Sqid.</summary>
    /// <param name="categorySqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<AuditCategoryDto>> GetByIdAsync(
        string categorySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one category by its stable <c>Code</c>.</summary>
    /// <param name="categoryCode">Stable category code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<AuditCategoryDto>> GetByCodeAsync(
        string categoryCode,
        CancellationToken cancellationToken = default);

    /// <summary>Lists categories (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<AuditCategoryPageDto>> ListAsync(
        AuditCategoryFilterDto filter,
        CancellationToken cancellationToken = default);
}
