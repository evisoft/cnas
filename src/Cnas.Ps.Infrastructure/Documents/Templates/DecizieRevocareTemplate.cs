using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Decizie de revocare</em> (revocation decision).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (IDNP + full name).</item>
///   <item>Reason of revocation (justified paragraph).</item>
///   <item>Effective-from date (UTC), bold.</item>
///   <item>Appeal-rights notice (italic, small 8pt) referencing the standard 30-day window.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>reason</c> — <see cref="string"/>.</item>
///   <item><c>effectiveFromUtc</c> — <see cref="DateTime"/> (UTC).</item>
/// </list>
/// </remarks>
public sealed class DecizieRevocareTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "decizie-revocare";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryIdnp", out var idnp) || string.IsNullOrWhiteSpace(idnp))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryIdnp.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryFullName", out var fullName) || string.IsNullOrWhiteSpace(fullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryFullName.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "reason", out var reason) || string.IsNullOrWhiteSpace(reason))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: reason.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "effectiveFromUtc", out var effectiveFromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: effectiveFromUtc.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("DECIZIE DE REVOCARE"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Către:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Reason (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Motivul revocării:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(reason, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Effective-from date. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Cu valabilitate de la: {DocxRenderHelpers.UtcDateFormat(effectiveFromUtc)}",
                bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Appeal-rights notice (italic, small 8pt = 16 half-points). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Prezenta decizie poate fi contestată în termen de 30 de zile de la "
                + "comunicare, la instanța de contencios administrativ competentă, "
                + "conform legislației în vigoare.",
                italic: true,
                fontSizeHalfPoints: "16"));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Director CNAS ____________________",
                alignment: JustificationValues.Right));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── UTC footer. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Generat automat la {DocxRenderHelpers.UtcFormat(DateTime.UtcNow)}",
                italic: true,
                fontSizeHalfPoints: "16"));

            mainPart.Document = new WordDocument(body);
            mainPart.Document.Save();
        }

        return Result<byte[]>.Success(ms.ToArray());
    }
}
