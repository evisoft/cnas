namespace Cnas.Ps.Contracts.Security;

/// <summary>
/// R0228 / TOR SEC 033 — machine-readable sensitivity classification carried on every
/// data field that crosses the system boundary. The label drives two downstream
/// behaviours: <c>X-CNAS-Sensitivity</c> response headers (so clients can render the
/// correct visual badge / audit decision) and the
/// <c>SENSITIVITY.RESTRICTED_ACCESS</c> audit row written by
/// <c>SensitivityHeaderMiddleware</c> when a Restricted field leaves the server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the labels live.</b> The label vocabulary is in <c>Cnas.Ps.Contracts</c>
/// rather than <c>Cnas.Ps.Core</c> so the attribute can be applied directly to DTOs
/// (which live in Contracts) without violating the architecture rule that forbids
/// Contracts→Core dependencies (see <c>LayerBoundaryTests.Contracts_HasNoOutboundDependencies</c>).
/// The semantic location is "domain"; the physical location is the assembly closest to
/// the consumers.
/// </para>
/// <para>
/// <b>Ordering matters.</b> The underlying integer values are deliberately ascending
/// (<c>Public &lt; Internal &lt; Confidential &lt; Restricted</c>) so resolver code
/// can take the maximum across a type's properties using ordinary
/// <see cref="System.Linq.Enumerable.Max{TSource}(System.Collections.Generic.IEnumerable{TSource})"/>
/// without writing a custom comparer.
/// </para>
/// </remarks>
public enum SensitivityLabel
{
    /// <summary>
    /// Anonymous-readable; no PII. Examples: service-catalog name, classifier
    /// descriptions, Sqid-encoded opaque ids that do not leak business volume.
    /// </summary>
    Public = 0,

    /// <summary>
    /// Non-PII but only authorised staff. Examples: internal task notes, workflow
    /// state codes, audit policy codes. This is the default for unannotated
    /// properties — the safe floor a field falls to when nobody has decided.
    /// </summary>
    Internal = 1,

    /// <summary>
    /// PII or business-sensitive data. Examples: most citizen attributes — full
    /// name, email, phone — and personal-finance figures such as the projected
    /// pension or contribution amounts.
    /// </summary>
    Confidential = 2,

    /// <summary>
    /// High-sensitivity. Examples: Moldovan IDNP, bank account numbers, decision
    /// rationale text, audit-log payloads. Crossing the boundary with a Restricted
    /// field triggers an audit row so disclosures are traceable end-to-end.
    /// </summary>
    Restricted = 3,
}
