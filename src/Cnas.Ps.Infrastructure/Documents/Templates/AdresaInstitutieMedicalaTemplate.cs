using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Adresă către instituție medicală</em> (formal letter
/// addressed to a medical institution requesting documentation or information).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Institution block (name + postal address).</item>
///   <item>Request body (justified paragraph).</item>
///   <item>Bullet list of attached documents.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>institutionName</c> — <see cref="string"/>.</item>
///   <item><c>institutionAddress</c> — <see cref="string"/>.</item>
///   <item><c>requestText</c> — <see cref="string"/>.</item>
///   <item><c>attachedDocs</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/> of <see cref="string"/>.</item>
/// </list>
/// </remarks>
public sealed class AdresaInstitutieMedicalaTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "adresa-institutie-medicala";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "institutionName", out var institutionName) || string.IsNullOrWhiteSpace(institutionName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: institutionName.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "institutionAddress", out var institutionAddress) || string.IsNullOrWhiteSpace(institutionAddress))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: institutionAddress.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "requestText", out var requestText) || string.IsNullOrWhiteSpace(requestText))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: requestText.");
        }

        // Accept both List<string> and IReadOnlyList<string> at the boundary.
        IReadOnlyList<string>? attachedDocs = null;
        if (facts.TryGetValue("attachedDocs", out var rawDocs))
        {
            attachedDocs = rawDocs switch
            {
                IReadOnlyList<string> roList => roList,
                IEnumerable<string> en => en.ToList(),
                _ => null,
            };
        }

        if (attachedDocs is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: attachedDocs.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("ADRESĂ CĂTRE INSTITUȚIE MEDICALĂ"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Institution block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Către:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(institutionName));
            body.AppendChild(DocxRenderHelpers.Paragraph(institutionAddress));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Request body (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(requestText, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Attached-documents bullet list. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Documente anexate:", bold: true));
            foreach (var doc in attachedDocs)
            {
                body.AppendChild(DocxRenderHelpers.Bullet(doc ?? string.Empty));
            }

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
