namespace Cnas.Ps.Infrastructure.Services.Prefill;

/// <summary>
/// R0552 / R0562 — pragmatic adapter contract overlaid on the R0363 gateway
/// implementations. The R0363 gateways return <c>ProfileRefreshDeltaDto</c>
/// (old-value / new-value pairs for the contributor-side writer); pre-fill needs
/// the same upstream data shaped as a simple field-name → value dictionary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate adapter contract.</b> Today's R0363 mocks return synthetic
/// delta sets. We could derive the pre-fill dictionary by mining the delta
/// payloads (the JSON in <c>PayloadJson</c>) but that couples two distinct
/// pipelines and forces every gateway to maintain delta-compatible JSON even when
/// it only needs to answer a pre-fill query. Layering a second small contract on
/// top keeps the two concerns decoupled — when real SOAP wiring lands each
/// production gateway implementation can declare both contracts.
/// </para>
/// <para>
/// <b>Field vocabulary.</b> Adapter implementations MUST return keys from
/// <c>Cnas.Ps.Application.Prefill.PrefillFields.All</c> only. Unknown keys are
/// silently dropped by the merge logic (defensive — bad data in production must
/// not crash the citizen-facing form).
/// </para>
/// </remarks>
public interface IPrefillSourceAdapter
{
    /// <summary>Stable upstream source code (<c>"RSP"</c>, <c>"RSUD"</c>, <c>"SI_SFS"</c>).</summary>
    string SourceCode { get; }

    /// <summary>
    /// Returns the field-name → value dictionary the adapter is willing to provide for
    /// the supplied IDNP. May return an empty dictionary (no data on file) — the merge
    /// logic treats that as a successful "nothing to say".
    /// </summary>
    /// <param name="idnp">The Solicitant's IDNP.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyDictionary<string, string>> FetchPrefillAsync(string idnp, CancellationToken ct);
}
