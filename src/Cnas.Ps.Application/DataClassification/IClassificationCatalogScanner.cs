using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.DataClassification;

/// <summary>
/// R2279 / TOR SEC 033 — reflection-based scanner over the loaded
/// <c>Cnas.Ps.Contracts</c>-family assemblies. Reads
/// <c>[SensitivityClassification]</c> attributes off public properties of
/// public types and returns the discovered list (plus aggregated counters)
/// to the catalog service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure read.</b> The scanner does NOT persist anything. The catalog
/// service is responsible for translating the scan outcome into snapshot +
/// entry rows.
/// </para>
/// <para>
/// <b>Reflection scope.</b> The scanner restricts itself to assemblies whose
/// simple name starts with <c>Cnas.Ps.Contracts</c>. This is a security
/// invariant: the scan output cross-references third-party types only if
/// future Contracts projects share the prefix. Documented in CLAUDE.md +
/// the iteration TOR (no PII; no third-party type leakage).
/// </para>
/// <para>
/// <b>Ordering.</b> The returned property list is sorted by
/// <c>TypeFullName ASC, PropertyName ASC</c> so tests + dashboards see a
/// deterministic projection.
/// </para>
/// </remarks>
public interface IClassificationCatalogScanner
{
    /// <summary>
    /// Performs one classification-catalog scan and returns the outcome.
    /// </summary>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>
    /// A success <see cref="Result{T}"/> wrapping the discovered properties +
    /// label counts. Failures are reserved for catastrophic reflection
    /// faults (e.g. <see cref="System.Reflection.ReflectionTypeLoadException"/>
    /// that yields no types).
    /// </returns>
    Task<Result<ClassificationCatalogScanOutcomeDto>> ScanAsync(CancellationToken cancellationToken = default);
}
