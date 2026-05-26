using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Classifiers;

/// <summary>
/// R0402 / TOR CF 17.09 — pre-flight guard that counts FK references into
/// <see cref="Cnas.Ps.Core.Domain.Classifier"/> rows before deactivation /
/// deletion is allowed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate service.</b> The Classifier table uses stable
/// <c>(Kind, Code)</c> natural keys, and referencing entities (Contributors,
/// declarations, addresses) store the code value rather than a synthetic FK.
/// EF Core therefore cannot enforce referential integrity at the DB level —
/// instead, every mutation that lowers a row's availability must first ask
/// the guard whether anything still cites it. The guard answers in a
/// depersonalised summary (<see cref="ClassifierReferenceScanResultDto"/>):
/// only entity-name + row-count tuples, never the underlying rows.
/// </para>
/// <para>
/// <b>Coverage contract.</b> The guard's mapping is hard-coded; each known
/// scheme code is paired with the list of CLR entities + columns that hold
/// values from that scheme. Schemes that are not yet mapped report zero
/// references and the caller MUST update the guard when adding a new
/// scheme — the integration tests pin the existing mappings, so new
/// schemes will hit a documented allow-list of "unmapped, please extend".
/// </para>
/// <para>
/// <b>Result.</b> Callers compare
/// <see cref="ClassifierReferenceScanResultDto.ReferencingRowCount"/>
/// against zero. A non-zero count must surface as
/// <c>Result.Failure(ErrorCodes.ClassifierReferenced, …)</c> at the calling
/// service; the guard itself never decides policy — it only counts.
/// </para>
/// </remarks>
public interface IClassifierReferenceGuard
{
    /// <summary>
    /// Counts every row across every known entity that references the
    /// supplied (<paramref name="schemeCode"/>, <paramref name="value"/>)
    /// classifier pair.
    /// </summary>
    /// <param name="schemeCode">
    /// Stable classifier scheme code (e.g. <c>CAEM</c>, <c>CUATM</c>,
    /// <c>COUNTRY</c>). Trim + invariant-uppercase canonicalised inside
    /// the guard.
    /// </param>
    /// <param name="value">
    /// Classifier code value within the scheme (e.g. <c>01.11</c>). Case-
    /// sensitive — the guard counts exact matches to mirror how the citing
    /// rows hold the code.
    /// </param>
    /// <param name="cancellationToken">Co-operative cancellation.</param>
    /// <returns>
    /// A successful <see cref="ClassifierReferenceScanResultDto"/> describing
    /// the per-entity breakdown. The guard never fails for business reasons
    /// — only an underlying I/O exception would bubble through.
    /// </returns>
    Task<Result<ClassifierReferenceScanResultDto>> ScanAsync(
        string schemeCode,
        string value,
        CancellationToken cancellationToken = default);
}
