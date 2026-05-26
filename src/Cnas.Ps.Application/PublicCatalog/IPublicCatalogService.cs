using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.PublicCatalog;

/// <summary>
/// R0502 / R0504 / R0505 / TOR CF 01.05 / CF 01.06 / CF 01.08 — public
/// services-catalog (ServicePassport) read-only façade. Drives both the JSON list
/// endpoint and the CSV export endpoint exposed under <c>/api/public-catalog</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Anonymous surface.</b> The catalog endpoint is unauthenticated; the service
/// MUST NOT leak any PII, draft passports (<c>IsCurrent=false</c>), or disabled
/// passports (<c>IsActive=false</c>). Only the current, active rows of each code
/// are exposed.
/// </para>
/// <para>
/// <b>Budget gate.</b> Both <see cref="ListAsync"/> and
/// <see cref="ExportCsvAsync"/> consult <see cref="IQueryBudgetService"/> against
/// the <c>PublicCatalog</c> registry; an over-budget call surfaces as a
/// <see cref="ErrorCodes.QueryTooBroad"/> failure carrying the budget verdict on
/// <see cref="LastBudgetVerdict"/> for the controller to harvest.
/// </para>
/// </remarks>
public interface IPublicCatalogService
{
    /// <summary>
    /// Paged list of current + active passport rows. Filters by <c>Q</c> (free-text,
    /// diacritic-insensitive), <c>Category</c> (equality), orders by the requested
    /// <c>Sort</c> key, and consults the budget guard before materialising.
    /// </summary>
    /// <param name="query">Filter envelope; nullable filters trigger budget hints.</param>
    /// <param name="ct">Cancellation token honoured throughout.</param>
    /// <returns>
    /// On success a paged result of <see cref="PublicCatalogListItemDto"/> rows. On
    /// budget refusal a <see cref="Result{T}.Failure"/> with code
    /// <see cref="ErrorCodes.QueryTooBroad"/>; the verdict can be recovered via
    /// <see cref="LastBudgetVerdict"/>. On validation failure a
    /// <see cref="ErrorCodes.ValidationFailed"/> result (e.g. malformed
    /// <c>Sort</c>).
    /// </returns>
    Task<Result<PagedResult<PublicCatalogListItemDto>>> ListAsync(
        PublicCatalogListQueryDto query,
        CancellationToken ct = default);

    /// <summary>
    /// Renders the filtered + sorted catalog as a CSV blob (UTF-8 with BOM,
    /// RFC 4180 quoting). The whole filtered set is exported — pagination is
    /// intentionally ignored, but the same budget guard as
    /// <see cref="ListAsync"/> still gates the call so over-budget exports are
    /// refused.
    /// </summary>
    /// <param name="query">Same filter envelope as <see cref="ListAsync"/>.</param>
    /// <param name="ct">Cancellation token honoured throughout.</param>
    /// <returns>
    /// On success a byte array containing the CSV payload. On budget refusal a
    /// <see cref="Result{T}.Failure"/> with code
    /// <see cref="ErrorCodes.QueryTooBroad"/>.
    /// </returns>
    Task<Result<byte[]>> ExportCsvAsync(
        PublicCatalogListQueryDto query,
        CancellationToken ct = default);

    /// <summary>
    /// Most-recent <see cref="QueryBudgetVerdict"/> produced by either
    /// <see cref="ListAsync"/> or <see cref="ExportCsvAsync"/> on this service
    /// instance. <c>null</c> when no call has been made yet. The controller
    /// inspects this slot after observing a
    /// <see cref="ErrorCodes.QueryTooBroad"/> failure to populate the
    /// ProblemDetails <c>extensions["budget"]</c> bag.
    /// </summary>
    /// <remarks>
    /// Per-request scoped lifetime makes the read safe — each HTTP request gets a
    /// fresh service instance and therefore a fresh verdict slot. Callers MUST NOT
    /// share an <see cref="IPublicCatalogService"/> instance across requests.
    /// </remarks>
    QueryBudgetVerdict? LastBudgetVerdict { get; }
}
