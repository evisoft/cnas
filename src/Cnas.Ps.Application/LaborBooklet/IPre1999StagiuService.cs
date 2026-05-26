using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.LaborBooklet;

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — service façade for the pre-1999 stagiu
/// Years/Months/Days roll-up table attached to an
/// <see cref="Cnas.Ps.Core.Domain.InsuredPerson"/>. Drives the data that the
/// pension calculator consumes for citizens whose employment history predates
/// the 01.01.1999 contribution-declarations pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="ILaborBookletService"/> which handles the raw
/// employment-period timeline attached to the natural-person Solicitant. This
/// service operates one level up the join graph: it appends, lists and removes
/// already-validated pre-1999 stagiu tallies on the InsuredPerson aggregate
/// itself.
/// </para>
/// <para>
/// All identifiers crossing the boundary are Sqid-encoded per CLAUDE.md
/// RULE 3.
/// </para>
/// </remarks>
public interface IPre1999StagiuService
{
    /// <summary>
    /// R0922 / Annex 2 §8.2.4 — appends a fresh pre-1999 stagiu row to the
    /// supplied InsuredPerson aggregate. Emits a Notice audit
    /// <c>PRE1999_STAGIU.APPENDED</c>.
    /// </summary>
    /// <param name="insuredSqid">Sqid-encoded id of the InsuredPerson.</param>
    /// <param name="input">Validated input envelope.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success the persisted DTO; <see cref="ErrorCodes.NotFound"/> when the
    /// InsuredPerson does not exist; <see cref="ErrorCodes.ValidationFailed"/>
    /// on date-range or numeric-bound violations;
    /// <see cref="ErrorCodes.InvalidSqid"/> on malformed Sqid.
    /// </returns>
    Task<Result<Pre1999StagiuDto>> AppendAsync(
        string insuredSqid,
        Pre1999StagiuInputDto input,
        CancellationToken ct = default);

    /// <summary>
    /// R0922 — soft-deletes the supplied stagiu row (sets
    /// <see cref="Cnas.Ps.Core.Domain.AuditableEntity.IsActive"/> to <c>false</c>).
    /// Emits a Notice audit <c>PRE1999_STAGIU.REMOVED</c>.
    /// </summary>
    /// <param name="recordSqid">Sqid-encoded id of the stagiu row.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success an empty success result; <see cref="ErrorCodes.NotFound"/> when
    /// the row does not exist; <see cref="ErrorCodes.InvalidSqid"/> on malformed
    /// Sqid.
    /// </returns>
    Task<Result> RemoveAsync(
        string recordSqid,
        CancellationToken ct = default);

    /// <summary>
    /// R0922 — lists every active pre-1999 stagiu row attached to the supplied
    /// InsuredPerson aggregate, ordered by ascending <c>FromDate</c>.
    /// </summary>
    /// <param name="insuredSqid">Sqid-encoded id of the InsuredPerson.</param>
    /// <param name="ct">Standard cancellation token.</param>
    /// <returns>
    /// On success a (possibly empty) read-only list of DTOs;
    /// <see cref="ErrorCodes.NotFound"/> when the InsuredPerson does not exist;
    /// <see cref="ErrorCodes.InvalidSqid"/> on malformed Sqid.
    /// </returns>
    Task<Result<IReadOnlyList<Pre1999StagiuDto>>> ListAsync(
        string insuredSqid,
        CancellationToken ct = default);
}
