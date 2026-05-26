using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>UC09 / UC19 — Extract / Generate reports.</summary>
public interface IReportingService
{
    /// <summary>
    /// Enumerates every report code recognised by <see cref="GenerateAsync"/> together with
    /// its display title in each supported UI language. Used by <c>GET /api/reports</c> to
    /// populate the report-picker drop-down without the client having to hard-code the
    /// catalogue.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> wrapping the list of catalogue entries. The list is
    /// deterministic but unordered — callers that need alphabetical ordering should sort
    /// by <see cref="ReportCatalogEntryOutput.Code"/> client-side.
    /// </returns>
    Task<Result<IReadOnlyList<ReportCatalogEntryOutput>>> ListAvailableAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Generates a report defined by its code with the supplied parameters JSON.</summary>
    Task<Result<Stream>> GenerateAsync(
        string reportCode,
        string parametersJson,
        ExportFormat format,
        CancellationToken cancellationToken = default);
}
