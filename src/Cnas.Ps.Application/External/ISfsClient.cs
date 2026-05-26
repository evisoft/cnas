using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.External;

/// <summary>
/// SFS — Serviciul Fiscal de Stat (State Tax Service). Source of salary declarations
/// (IPC18/forma) submitted by employers. CNAS-PS uses SFS data to validate insured-person
/// contribution periods and amounts.
/// </summary>
/// <remarks>
/// Routes through MConnect using service code <c>SFS.GetSalaryDeclarations</c>. See TOR
/// §2.1 (item 3 in the list of 11 external systems). The returned list may be empty when
/// no declarations exist for the supplied IDNP/year.
/// </remarks>
public interface ISfsClient
{
    /// <summary>
    /// Retrieves the monthly salary declarations filed by all employers for the supplied
    /// IDNP within the supplied calendar year.
    /// </summary>
    /// <param name="idnp">Insured person's IDNP.</param>
    /// <param name="year">Calendar year (e.g. 2025) — must be ≥ 2000.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}"/> wrapping the declarations on success; an empty list is a
    /// valid success outcome (no declarations filed).
    /// </returns>
    Task<Result<IReadOnlyList<SfsDeclaration>>> GetDeclarationsAsync(string idnp, int year, CancellationToken ct = default);
}

/// <summary>
/// One row of a monthly salary declaration as reported by SFS.
/// </summary>
/// <param name="Year">Calendar year the declaration covers.</param>
/// <param name="Month">Calendar month (1-12) the declaration covers.</param>
/// <param name="GrossSalaryMdl">Gross salary in MDL for the period.</param>
/// <param name="ContributionMdl">Social contribution amount in MDL for the period.</param>
/// <param name="EmployerIdno">IDNO of the employer who filed the declaration.</param>
public sealed record SfsDeclaration(int Year, int Month, decimal GrossSalaryMdl, decimal ContributionMdl, string EmployerIdno);
