using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0115 / TOR CF 14.07 — admin-facing MNotify template registry. Supports
/// CRUD (list / get / upsert / deactivate) for the per-channel message
/// templates the platform dispatches through MNotify.
/// </summary>
public interface IMNotifyTemplateService
{
    /// <summary>Lists active (and optionally inactive) templates.</summary>
    /// <param name="includeInactive">When <c>true</c> the result includes deactivated rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Templates sorted by <c>Code</c>.</returns>
    Task<Result<IReadOnlyList<MNotifyTemplateDto>>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a single template by Sqid.</summary>
    /// <param name="sqid">Sqid-encoded id of the template.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The DTO on success; <see cref="ErrorCodes.NotFound"/> if unknown.</returns>
    Task<Result<MNotifyTemplateDto>> GetAsync(
        string sqid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new template or updates the existing row keyed by
    /// <see cref="MNotifyTemplateInputDto.Code"/>.
    /// </summary>
    /// <param name="input">Template payload (validated by the application boundary).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted DTO on success.</returns>
    Task<Result<MNotifyTemplateDto>> UpsertAsync(
        MNotifyTemplateInputDto input,
        CancellationToken cancellationToken = default);

    /// <summary>Deactivates an existing template. Idempotent.</summary>
    /// <param name="sqid">Sqid-encoded id of the template.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success on flip; <see cref="ErrorCodes.NotFound"/> if unknown.</returns>
    Task<Result> DeactivateAsync(
        string sqid,
        CancellationToken cancellationToken = default);
}
