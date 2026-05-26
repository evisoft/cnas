using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// UC16 — Configure workflow. Workflow definitions are stored as JSON and applied at
/// runtime. R0129 / CF 15.04 — definitions are versioned and append-only.
/// </summary>
public interface IWorkflowConfigurationService
{
    /// <summary>Returns the current (<c>IsCurrent=true</c>) workflow definition JSON by code.</summary>
    /// <param name="workflowCode">Stable workflow code (case-insensitive).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>The JSON document of the active definition.</returns>
    Task<Result<string>> GetDefinitionAsync(string workflowCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new version of the workflow definition; previous versions remain
    /// queryable. Writes a Critical <c>WORKFLOWDEFINITION.VERSION_CREATED</c> audit row
    /// capturing the from/to version numbers.
    /// </summary>
    /// <param name="workflowCode">Stable workflow code (case-insensitive).</param>
    /// <param name="definitionJson">New JSON payload — validated to be well-formed JSON.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Success on insert; no-op success when the JSON is byte-equal to the current row.</returns>
    Task<Result> SaveDefinitionAsync(string workflowCode, string definitionJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// R0129 / CF 15.04 — returns all historical versions of <paramref name="workflowCode"/>
    /// ordered by <see cref="Cnas.Ps.Core.Domain.WorkflowDefinition.Version"/> DESC. Each
    /// entry is a compact summary (no JSON body) — fetch the full row by direct DB query
    /// or extend the contract later if a "show diff between version N and N-1" feature
    /// needs the bodies.
    /// </summary>
    /// <param name="workflowCode">Stable workflow code (case-insensitive).</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>History entries, newest revision first.</returns>
    Task<Result<IReadOnlyList<Contracts.WorkflowDefinitionHistoryItem>>> GetHistoryAsync(
        string workflowCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// R0121 / CF 16.02 — returns every workflow whose current (IsCurrent=true)
    /// definition exists, ordered alphabetically by code. Used by the
    /// admin visual designer to populate its list page. Optional
    /// <paramref name="codeFilter"/> applies a case-insensitive
    /// <c>CONTAINS</c> match against <c>Code</c>; empty / null skips the filter.
    /// </summary>
    /// <param name="codeFilter">Optional free-text filter; matched case-insensitively against <c>Code</c>.</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// Success with the list of <see cref="Contracts.WorkflowDefinitionListItem"/>
    /// rows (possibly empty); never <see cref="ErrorCodes.NotFound"/> — an empty
    /// table is a legitimate empty result.
    /// </returns>
    Task<Result<IReadOnlyList<Contracts.WorkflowDefinitionListItem>>> ListCurrentAsync(
        string? codeFilter = null,
        CancellationToken cancellationToken = default);
}
