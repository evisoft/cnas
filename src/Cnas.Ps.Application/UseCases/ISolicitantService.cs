using Cnas.Ps.Application.Qbe;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Contracts.Search;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0167 — Solicitant registry list/search façade. Currently exposes only the list
/// endpoint that R0167 wired the query-budget guard into; CRUD is owned by other
/// pathways (MConnect sync, application-form intake) and is intentionally not part of
/// this façade.
/// </summary>
/// <remarks>
/// <para>
/// <b>Result&lt;T&gt; envelope.</b> Business failures (validation, budget rejection)
/// surface as <see cref="Result{T}"/> failures with stable error codes. When the budget
/// guard refuses a query, the failure code is
/// <see cref="ErrorCodes.QueryTooBroad"/>; callers recover the verdict via
/// <see cref="LastBudgetVerdict"/> on the same per-request service instance.
/// </para>
/// <para>
/// <b>Sqid invariant.</b> Every id crossing this façade is a Sqid string. Internal
/// long ids never leak out.
/// </para>
/// </remarks>
public interface ISolicitantService
{
    /// <summary>
    /// Paged list of solicitants matching the supplied filter. Consults the
    /// <see cref="IQueryBudgetService"/> before materialising and returns a
    /// <see cref="ErrorCodes.QueryTooBroad"/> failure when the budget refuses the
    /// query.
    /// </summary>
    /// <param name="input">List filter parameters; nullable filter fields trigger the budget hints.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success a page of <see cref="SolicitantListItem"/> rows. On budget failure a
    /// <see cref="Result{T}.Failure"/> with code <see cref="ErrorCodes.QueryTooBroad"/>;
    /// the verdict can be recovered via <see cref="LastBudgetVerdict"/>.
    /// </returns>
    Task<Result<PagedResult<SolicitantListItem>>> ListAsync(
        SolicitantListQueryInput input,
        CancellationToken ct = default);

    /// <summary>
    /// R0163 — variant of <see cref="ListAsync(SolicitantListQueryInput, CancellationToken)"/>
    /// that accepts a typed <see cref="QbeFilter"/>. The QBE predicate is converted to a
    /// LINQ expression via <see cref="IQbeToLinqConverter"/> and applied BEFORE the budget
    /// guard runs, so an over-filtered query consumes the budget the same way as the
    /// query-string list path.
    /// </summary>
    /// <param name="input">
    /// Paging + free-text filter inputs. The QBE filter narrows the result set further;
    /// callers commonly pass <see cref="SolicitantListQueryInput"/> with default values
    /// when QBE drives every condition.
    /// </param>
    /// <param name="qbe">
    /// QBE envelope. <see langword="null"/> or empty matches everything. Returns a
    /// <see cref="Result{T}.Failure"/> with one of the <c>QBE_*</c> error codes on
    /// invalid input.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Paged list on success, or a <see cref="Result{T}.Failure"/> with a stable
    /// <see cref="ErrorCodes.QueryTooBroad"/> / <see cref="ErrorCodes.QbeFieldNotQueryable"/> /
    /// related QBE error code.
    /// </returns>
    Task<Result<PagedResult<SolicitantListItem>>> SearchAsync(
        SolicitantListQueryInput input,
        QbeFilter? qbe,
        CancellationToken ct = default);

    /// <summary>
    /// Most recent <see cref="QueryBudgetVerdict"/> produced by <see cref="ListAsync"/>
    /// on this service instance. <c>null</c> when no list call has been made yet, or
    /// when the most recent call did not consult the budget guard. The controller
    /// reads this field after observing a <see cref="ErrorCodes.QueryTooBroad"/>
    /// failure to populate the ProblemDetails <c>extensions["budget"]</c> slot.
    /// </summary>
    /// <remarks>
    /// Per-request scoped lifetime makes the read safe — each HTTP request gets a
    /// fresh service instance and therefore a fresh verdict slot. Callers MUST NOT
    /// share an <see cref="ISolicitantService"/> instance across requests.
    /// </remarks>
    QueryBudgetVerdict? LastBudgetVerdict { get; }

    /// <summary>
    /// R0525 / TOR CF 03.08 — most recent list of refinement suggestions emitted by
    /// the search call on this service instance. <c>null</c> when no call has been
    /// made yet or when the row count was below the suggestion threshold. The
    /// controller reads this field after a successful search to populate the
    /// <see cref="Cnas.Ps.Contracts.Search.SearchSuggestionDto"/> array in the wire
    /// response.
    /// </summary>
    /// <remarks>
    /// Per-request scoped lifetime makes the read safe — each HTTP request gets a
    /// fresh service instance. Callers MUST NOT share an
    /// <see cref="ISolicitantService"/> instance across requests.
    /// </remarks>
    IReadOnlyList<SearchSuggestionDto>? LastSuggestions { get; }

    /// <summary>
    /// R0623 / TOR CF 13.04 — pre-flight scan that counts OPEN-state foreign
    /// references to the targeted Solicitant. Wraps
    /// <c>Cnas.Ps.Application.Solicitants.ISolicitantReferenceGuard.ScanAsync</c>
    /// so the admin UI can preview the block-or-allow verdict before
    /// attempting a deactivation. Safe to call repeatedly — the underlying
    /// guard is pure-read.
    /// </summary>
    /// <param name="solicitantSqid">Sqid-encoded id of the Solicitant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// On success a <see cref="SolicitantReferenceScanDto"/> with per-table
    /// OPEN counts. <see cref="ErrorCodes.InvalidSqid"/> when the Sqid cannot
    /// be decoded; <see cref="ErrorCodes.NotFound"/> when the decoded id
    /// does not correspond to an active Solicitant row.
    /// </returns>
    Task<Result<SolicitantReferenceScanDto>> ScanReferencesAsync(
        string solicitantSqid,
        CancellationToken ct = default);

    /// <summary>
    /// R0623 / TOR CF 13.04 — soft-deactivates an existing
    /// <see cref="Cnas.Ps.Core.Domain.Solicitant"/> row (flips
    /// <c>IsActive=false</c>) AFTER asserting via
    /// <c>ISolicitantReferenceGuard</c> that no OPEN-state foreign references
    /// would be orphaned by the transition. A non-zero
    /// <see cref="SolicitantReferenceScanDto.TotalOpen"/> short-circuits with
    /// <see cref="ErrorCodes.SolicitantReferencedByOpenRecords"/> and the row
    /// is NOT mutated.
    /// </summary>
    /// <param name="solicitantSqid">Sqid-encoded id of the Solicitant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the row was deactivated (or was
    /// already inactive — idempotent);
    /// <see cref="ErrorCodes.InvalidSqid"/> on a malformed Sqid;
    /// <see cref="ErrorCodes.NotFound"/> when no matching row exists;
    /// <see cref="ErrorCodes.SolicitantReferencedByOpenRecords"/> when one or
    /// more OPEN-state references would be orphaned by the deactivation.
    /// </returns>
    Task<Result> DeactivateAsync(
        string solicitantSqid,
        CancellationToken ct = default);
}
