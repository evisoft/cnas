using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0932 / TOR §10.1 — Application-layer port that re-runs the Fișa de calcul
/// formula evaluator after an operator edits the pre-filled row set. The MVP
/// implementation performs a sum-of-rows aggregation; future revisions may
/// switch to the iter-145 mass-recalc evaluator without changing the wire
/// contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sqid contract.</b> Input + output DTOs use a Sqid-encoded dossier id
/// (CLAUDE.md RULE 3). The service never returns raw <c>int</c>/<c>long</c> ids.
/// </para>
/// <para>
/// <b>Pure recalculation.</b> The service does NOT persist its result
/// automatically — that is the editor view's responsibility (separate Save
/// endpoint). This separation matches the "preview-then-commit" UX the
/// operator expects.
/// </para>
/// </remarks>
public interface IFisaDeCalculRecalculator
{
    /// <summary>
    /// Re-runs the Fișa de calcul formula evaluator against the supplied edited
    /// rows. The MVP rule is a sum aggregation; negative rows are rejected with
    /// <see cref="ErrorCodes.ValidationFailed"/>.
    /// </summary>
    /// <param name="input">Operator-edited row set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping a <see cref="FisaDeCalculRecalcResultDto"/>
    /// on success. Failure with <see cref="ErrorCodes.ValidationFailed"/> when
    /// the input envelope is malformed (null, empty, or contains a negative
    /// row).
    /// </returns>
    Task<Result<FisaDeCalculRecalcResultDto>> RecalculateAsync(
        FisaDeCalculRecalcInputDto input,
        CancellationToken cancellationToken = default);
}
