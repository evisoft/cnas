using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.UseCases;

/// <summary>
/// R0143 / CF 17.19 — assembles the eight-column configuration matrix for a service
/// passport (Form + Validation + MandatoryAttachments + Receipt + DecisionTemplate +
/// FișaCalcul + CalcFormulas + ProcessingRules + PrintForm).
/// </summary>
/// <remarks>
/// <para>
/// The matrix is a read-only projection — every value is sourced from existing storage
/// (the passport row, its associated <c>DocumentTemplate</c> rows by code convention,
/// and the new <c>MandatoryAttachmentsJson</c> / <c>CalcFormulasJson</c> columns
/// introduced in iter-121). No write surface is exposed here; admin edits flow through
/// <see cref="IServicePassportService.UpsertAsync"/> which versions the row.
/// </para>
/// </remarks>
public interface IServicePassportConfigMatrixService
{
    /// <summary>
    /// Returns the full configuration matrix for the passport identified by
    /// <paramref name="passportCode"/> (case-insensitive). The catalogue's current
    /// revision is consulted — historical revisions are not addressable through this
    /// surface (use the standard detail / history endpoints to read past revisions).
    /// </summary>
    /// <param name="passportCode">Logical passport code (e.g. <c>SP-3.1-A-BIRTH-GRANT</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Success with the full matrix; failure with <see cref="ErrorCodes.NotFound"/>
    /// when no active current passport row matches the supplied code.
    /// </returns>
    Task<Result<ServicePassportConfigMatrixDto>> GetMatrixAsync(
        string passportCode,
        CancellationToken cancellationToken = default);
}
