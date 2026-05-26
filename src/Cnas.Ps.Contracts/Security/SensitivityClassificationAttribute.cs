using System;

namespace Cnas.Ps.Contracts.Security;

/// <summary>
/// R0228 / TOR SEC 033 — declares the sensitivity classification of a class, property,
/// field, or parameter. Read at runtime by <c>ISensitivityResolver</c> implementations
/// and surfaced by the <c>SensitivityHeaderMiddleware</c> on every API response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where to put it.</b> Apply at the <i>property</i> level for granular control
/// (the common case); apply at the <i>class</i> level to set a floor for every
/// property on the type — useful when the entire DTO is uniformly sensitive
/// (e.g. an audit-trail payload that should never read below Confidential).
/// </para>
/// <para>
/// <b>Inheritance.</b> The attribute is inheritable
/// (<see cref="AttributeUsageAttribute.Inherited"/> = <c>true</c>) so derived DTOs
/// pick up the base type's floor automatically — convenient for shared base records
/// such as <c>PagedResult&lt;T&gt;</c> envelopes.
/// </para>
/// <para>
/// <b>Not a security mechanism.</b> Like Sqids, this attribute is metadata for
/// observability and governance; it does NOT prevent serialisation, mask values, or
/// enforce authorisation. Authorisation is the job of the controller / service layer.
/// The label exists so the system can tell the client "the payload you just
/// received carries Restricted data — audit accordingly".
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = true)]
public sealed class SensitivityClassificationAttribute : Attribute
{
    /// <summary>
    /// Creates a classification attribute carrying the supplied <paramref name="label"/>.
    /// </summary>
    /// <param name="label">The sensitivity level applied to the annotated symbol.</param>
    public SensitivityClassificationAttribute(SensitivityLabel label)
    {
        Label = label;
    }

    /// <summary>The sensitivity level applied to the annotated symbol.</summary>
    public SensitivityLabel Label { get; }

    /// <summary>
    /// Optional human-readable justification documenting why the symbol carries this
    /// label. Surfaced by <c>SensitivityResolver</c> diagnostics (R0228 audit explorer).
    /// </summary>
    public string? Reason { get; init; }
}
