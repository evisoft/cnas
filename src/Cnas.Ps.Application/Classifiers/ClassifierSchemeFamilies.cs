namespace Cnas.Ps.Application.Classifiers;

/// <summary>
/// R0401 / TOR CF 17.02-04 — classifier scheme families. The TOR splits the
/// classifier universe into three buckets that have different governance
/// rules inside SI PS:
/// <list type="bullet">
///   <item>
///     <see cref="National"/> — official national registers (CAEM Rev.2,
///     CUATM, CFOJ, CFP, NCM) mirrored from BNS / ASP / MConnect feeds.
///     Rows are inbound-only; SI PS rejects local mutations with the stable
///     code <c>CLASSIFIER.READONLY_MIRROR</c>.
///   </item>
///   <item>
///     <see cref="Interop"/> — schemes consumed by external system contracts
///     (RSP/RSUD message envelopes, integration enums). Locally writable but
///     governance is shared with the partner system that defined the code.
///   </item>
///   <item>
///     <see cref="Internal"/> — SI PS-owned schemes (DECISION_TYPE,
///     NOTIFICATION_CHANNEL, etc.) the CNAS administrator manages directly.
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// The discriminator lives in the Application layer (not Core) because Core
/// must stay dependency-free. Service callers look up a row's family via
/// <see cref="ClassifierSchemeFamilies.LookupFamily(string)"/> using its
/// <c>SchemeCode</c>.
/// </remarks>
public enum ClassifierSchemeFamily
{
    /// <summary>SI PS-owned scheme; freely mutable by the administrator.</summary>
    Internal = 0,

    /// <summary>Interop-shared scheme; locally writable but governance is shared.</summary>
    Interop = 1,

    /// <summary>Read-only mirror of an official national register.</summary>
    National = 2,
}

/// <summary>
/// R0401 / TOR CF 17.02-04 — scheme-code constants for the five official
/// national classifiers SI PS mirrors locally plus the lookup helper. Adding
/// a new national scheme means adding its code to <see cref="NationalSchemes"/>
/// and seeding rows via <c>NationalClassifiersSeed</c>.
/// </summary>
public static class ClassifierSchemeFamilies
{
    /// <summary>CAEM Rev.2 — national economic-activity classifier.</summary>
    public const string Caem = "CAEM_REV2";

    /// <summary>CUATM — Moldovan administrative-territorial classifier.</summary>
    public const string Cuatm = "CUATM";

    /// <summary>CFOJ — occupations classifier (Clasificatorul ocupațiilor).</summary>
    public const string Cfoj = "CFOJ";

    /// <summary>CFP — legal-form classifier (Clasificatorul formelor juridice).</summary>
    public const string Cfp = "CFP";

    /// <summary>NCM — currency classifier (Nomenclatorul valutar).</summary>
    public const string Ncm = "NCM";

    /// <summary>Stable list of every national scheme this build recognises.</summary>
    public static readonly System.Collections.Generic.IReadOnlyList<string> NationalSchemes =
    [
        Caem,
        Cuatm,
        Cfoj,
        Cfp,
        Ncm,
    ];

    /// <summary>
    /// Looks up the family for a given scheme code. National schemes are
    /// recognised exactly; everything else is treated as <see cref="ClassifierSchemeFamily.Internal"/>
    /// today (the interop bucket is reserved for an explicit allow-list when
    /// the first cross-system scheme registers — currently empty).
    /// </summary>
    /// <param name="schemeCode">Scheme code (e.g. <c>CAEM_REV2</c>) to classify.</param>
    /// <returns>The matching <see cref="ClassifierSchemeFamily"/>.</returns>
    public static ClassifierSchemeFamily LookupFamily(string? schemeCode)
    {
        if (string.IsNullOrWhiteSpace(schemeCode))
        {
            return ClassifierSchemeFamily.Internal;
        }

        foreach (var code in NationalSchemes)
        {
            if (string.Equals(code, schemeCode, System.StringComparison.OrdinalIgnoreCase))
            {
                return ClassifierSchemeFamily.National;
            }
        }

        return ClassifierSchemeFamily.Internal;
    }
}
