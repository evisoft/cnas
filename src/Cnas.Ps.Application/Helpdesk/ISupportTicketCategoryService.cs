using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Helpdesk;

/// <summary>
/// R2500 / TOR PIR 020-023 — admin-facing registry for
/// <c>SupportTicketCategory</c> rows. CRUD + Activate / Deactivate lifecycle
/// for the helpdesk category catalog. Soft-deletes only (the row sticks
/// around with <c>IsActive=false</c> so historical tickets keep their FK
/// target).
/// </summary>
public interface ISupportTicketCategoryService
{
    /// <summary>Stable failure code: a category with the same <c>Code</c> already exists.</summary>
    public const string DuplicateCategoryCodeCode = "TICKET.CATEGORY.DUPLICATE_CODE";

    /// <summary>Stable failure code: the requested transition is not legal for the current state.</summary>
    public const string InvalidTransitionCode = "TICKET.CATEGORY.INVALID_TRANSITION";

    /// <summary>Stable audit event code emitted when a category is created.</summary>
    public const string AuditCategoryCreated = "TICKET.CATEGORY.CREATED";

    /// <summary>Stable audit event code emitted when a category is modified.</summary>
    public const string AuditCategoryModified = "TICKET.CATEGORY.MODIFIED";

    /// <summary>Stable audit event code emitted when a category transitions state (activate / deactivate).</summary>
    public const string AuditCategoryTransitioned = "TICKET.CATEGORY.TRANSITIONED";

    /// <summary>Creates a new helpdesk category.</summary>
    /// <param name="input">Create payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created DTO on success.</returns>
    Task<Result<SupportTicketCategoryDto>> CreateAsync(
        SupportTicketCategoryCreateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Modifies an existing category.</summary>
    /// <param name="categorySqid">Sqid-encoded category id.</param>
    /// <param name="input">Modify payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SupportTicketCategoryDto>> ModifyAsync(
        string categorySqid,
        SupportTicketCategoryModifyInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Activates a category.</summary>
    /// <param name="categorySqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SupportTicketCategoryDto>> ActivateAsync(
        string categorySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Deactivates a category.</summary>
    /// <param name="categorySqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated DTO on success.</returns>
    Task<Result<SupportTicketCategoryDto>> DeactivateAsync(
        string categorySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one category by Sqid.</summary>
    /// <param name="categorySqid">Sqid-encoded category id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<SupportTicketCategoryDto>> GetByIdAsync(
        string categorySqid,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one category by its stable <c>Code</c>.</summary>
    /// <param name="categoryCode">Stable category code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success.</returns>
    Task<Result<SupportTicketCategoryDto>> GetByCodeAsync(
        string categoryCode,
        CancellationToken cancellationToken = default);

    /// <summary>Lists categories (paged + filterable).</summary>
    /// <param name="filter">Filter envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page on success.</returns>
    Task<Result<SupportTicketCategoryPageDto>> ListAsync(
        SupportTicketCategoryFilterDto filter,
        CancellationToken cancellationToken = default);
}
