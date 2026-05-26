using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

// ────────────────────────────────────────────────────────────────────────────
// R0341 / TOR CF 11.06 — DTOs for the PDF/A conversion contract. The actual
// conversion library is externally gated; these DTOs ship now so the rest of
// the system has a stable contract to bind against.
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// R0341 — supported PDF/A conformance level for the conversion service. The
/// canonical CNAS choice is <see cref="Pdf_A_2u"/>: ISO 19005-2 level "u"
/// (Unicode-mappable text); the other constants surface for forward
/// compatibility when operators need an alternative profile.
/// </summary>
/// <remarks>
/// Numeric values are part of the persistence contract — renumbering is a
/// breaking change.
/// </remarks>
#pragma warning disable CA1707 // Identifiers should not contain underscores
public enum PdfAConformanceLevel
{
    /// <summary>ISO 19005-1 level B — basic visual fidelity.</summary>
    Pdf_A_1b = 0,

    /// <summary>ISO 19005-2 level B — basic visual fidelity, PDF 1.7 base.</summary>
    Pdf_A_2b = 1,

    /// <summary>ISO 19005-2 level U — Unicode-mappable text. CNAS canonical.</summary>
    Pdf_A_2u = 2,

    /// <summary>ISO 19005-3 level B — PDF 1.7 with attachments.</summary>
    Pdf_A_3b = 3,
}
#pragma warning restore CA1707

/// <summary>
/// R0341 — input envelope for an <see cref="PdfAConformanceLevel"/> conversion
/// request. The caller supplies a raw PDF byte array + the target conformance
/// level; the service returns the converted bytes + a fresh SHA-256.
/// </summary>
/// <param name="SourcePdfBytes">
/// Raw bytes of the input PDF. Bounded by the validator to 1..50_000_000 bytes.
/// </param>
/// <param name="TargetConformance">Target conformance level. Defaults to <see cref="PdfAConformanceLevel.Pdf_A_2u"/>.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record PdfAConversionInputDto(
    byte[] SourcePdfBytes,
    PdfAConformanceLevel TargetConformance = PdfAConformanceLevel.Pdf_A_2u);

/// <summary>
/// R0341 — outcome envelope returned by a successful PDF/A conversion. Carries
/// the converted bytes + the lower-case hex SHA-256 the caller should persist
/// on the corresponding <c>Document</c> row's content-hash column.
/// </summary>
/// <param name="ConvertedPdfBytes">Converted PDF bytes (already buffered).</param>
/// <param name="ConvertedSha256Hex">Lower-case hex SHA-256 of <paramref name="ConvertedPdfBytes"/>.</param>
/// <param name="ConformanceAchieved">Stable enum-name of the achieved conformance level.</param>
[SensitivityClassification(SensitivityLabel.Internal)]
public sealed record PdfAConversionOutcomeDto(
    byte[] ConvertedPdfBytes,
    string ConvertedSha256Hex,
    string ConformanceAchieved);
