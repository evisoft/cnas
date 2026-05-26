namespace Cnas.Ps.Core.Domain;

/// <summary>
/// Nomenclator / Clasificator — reference data row. TOR §2.3 #12, UC17.
/// </summary>
/// <remarks>
/// One row per (kind, code) pair. Sources are either national (CAEM, CUATM, CFOJ) and
/// synced from BNS via MConnect, or internal to SI PS and managed by the administrator.
/// </remarks>
public sealed class Classifier : AuditableEntity
{
    /// <summary>Logical kind, e.g. <c>CAEM</c>, <c>CUATM</c>, <c>CFOJ</c>, <c>DECISION_TYPE</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>Stable code (within the kind).</summary>
    public required string Code { get; set; }

    /// <summary>Localised label in Romanian.</summary>
    public required string LabelRo { get; set; }

    /// <summary>Localised label in English.</summary>
    public string? LabelEn { get; set; }

    /// <summary>Localised label in Russian.</summary>
    public string? LabelRu { get; set; }

    /// <summary>Optional parent code, for hierarchical classifiers like CAEM.</summary>
    public string? ParentCode { get; set; }

    /// <summary>Source — <c>internal</c>, <c>bns</c>, <c>asp</c>, etc.</summary>
    public required string Source { get; set; }

    /// <summary>
    /// R0401 / TOR CF 17.02-04 — true when the row is a local mirror of an
    /// official national register (CAEM Rev.2, CUATM, CFOJ, CFP, NCM) and
    /// must not be edited inside SI PS. National-mirror rows are owned by
    /// their upstream source (BNS / ASP / MConnect feeds) and propagated
    /// inbound only. Defaults to <c>false</c> so legacy internal rows behave
    /// unchanged. <c>IClassifierService.UpsertAsync</c> /
    /// <c>DeactivateAsync</c> reject mutations against rows where this flag
    /// is <c>true</c> with the stable error code
    /// <c>CLASSIFIER.READONLY_MIRROR</c>.
    /// </summary>
    public bool IsReadOnlyMirror { get; set; }
}
