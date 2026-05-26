using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Aviz pentru Avocatul Poporului</em>
/// (legal notice to the Ombudsperson).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Formal greeting: <c>Către Avocatul Poporului</c>.</item>
///   <item>Body paragraph summarising the case (justified).</item>
///   <item>Bullet list of attached documents.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded — never decode here; CLAUDE.md RULE 3).</item>
///   <item><c>caseSummary</c> — <see cref="string"/>.</item>
///   <item><c>attachedDocs</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/> of <see cref="string"/>.</item>
/// </list>
/// </remarks>
public sealed class AvizAvocatuluiPoporuluiTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "aviz-avocatul-poporului";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "dossierSqid", out var dossierSqid) || string.IsNullOrWhiteSpace(dossierSqid))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: dossierSqid.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "caseSummary", out var caseSummary) || string.IsNullOrWhiteSpace(caseSummary))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: caseSummary.");
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

            body.AppendChild(DocxRenderHelpers.Heading("AVIZ PENTRU AVOCATUL POPORULUI"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Formal greeting. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Către Avocatul Poporului,", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Body. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Cu referire la dosarul cu identificatorul {dossierSqid}, vă comunicăm "
                + "următorul rezumat al cazului:",
                alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(caseSummary, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Attached documents (bullet list). ──
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
