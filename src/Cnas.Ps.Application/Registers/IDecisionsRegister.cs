using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Registers;

/// <summary>
/// R1601 / TOR Annex 3.9 — read-only projection over the existing
/// <c>Document</c> aggregate that exposes the canonical "Registrul deciziilor"
/// (decisions register) shape. Not a separate table — the projection materialises
/// the Annex-3.9 columns from the rows where <c>Document.Kind = Decision</c>.
/// </summary>
/// <remarks>
/// <para>
/// Backed by <see cref="Application.Abstractions.IReadOnlyCnasDbContext"/> so the
/// listing reads route to the Postgres streaming replica per ARH 025. Replica
/// lag means a freshly-issued decision may not be visible immediately — the
/// register is eventually-consistent.
/// </para>
/// </remarks>
public interface IDecisionsRegister
{
    /// <summary>
    /// Returns a paged snapshot of the decisions register, narrowed by
    /// <paramref name="filter"/>. Ordering is <c>IssuedAtUtc DESC</c> so the
    /// most-recent decisions surface first.
    /// </summary>
    /// <param name="filter">Optional issuance-window + type filter.</param>
    /// <param name="page">1-based page index (clamped to ≥ 1).</param>
    /// <param name="pageSize">Page size (clamped to [1, 200]).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success"/> with the paged result;
    /// <see cref="Result{T}.Failure"/> with <see cref="ErrorCodes.ValidationFailed"/>
    /// on an invalid window (<c>FromUtc &gt; ToUtc</c>).
    /// </returns>
    Task<Result<PagedResult<DecisionRegisterRowDto>>> ListAsync(
        DecisionRegisterFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
