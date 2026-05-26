using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Cerere documente lipsă</em> (request for missing documents).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (full name, postal address).</item>
///   <item>Body paragraph (justified) referencing the dossier and explaining the
///   missing-documents situation.</item>
///   <item>Bullet list of missing documents — each item may include the document name
///   and optional issuing authority.</item>
///   <item>Deadline date (UTC) by which the documents must be submitted.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryAddress</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded — never decode here; CLAUDE.md RULE 3).</item>
///   <item><c>missingDocs</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/> of <see cref="string"/>.</item>
///   <item><c>deadlineUtc</c> — <see cref="DateTime"/> (UTC).</item>
/// </list>
/// </remarks>
public sealed class CerereDocumenteLipsaTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "cerere-documente-lipsa";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // ── Required-fact validation. The offending key is surfaced in the message so
        //    the caller can correct the call without a debugger. ──
        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryFullName", out var fullName) || string.IsNullOrWhiteSpace(fullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryFullName.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryAddress", out var address) || string.IsNullOrWhiteSpace(address))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryAddress.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "dossierSqid", out var dossierSqid) || string.IsNullOrWhiteSpace(dossierSqid))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: dossierSqid.");
        }

        // Accept both List<string> and IReadOnlyList<string> at the boundary (mirrors
        // InvitatieDocumenteSuplimentareTemplate so callers may pass either shape).
        IReadOnlyList<string>? missingDocs = null;
        if (facts.TryGetValue("missingDocs", out var rawDocs))
        {
            missingDocs = rawDocs switch
            {
                IReadOnlyList<string> roList => roList,
                IEnumerable<string> en => en.ToList(),
                _ => null,
            };
        }

        if (missingDocs is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: missingDocs.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "deadlineUtc", out var deadlineUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: deadlineUtc.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("CERERE DOCUMENTE LIPSĂ"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Către:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(fullName));
            body.AppendChild(DocxRenderHelpers.Paragraph(address));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Body (justified). dossierSqid is already opaque per CLAUDE.md RULE 3. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"În legătură cu dosarul Dvs. cu identificatorul {dossierSqid}, vă "
                + "informăm că la examinarea documentației prezentate s-a constatat lipsa "
                + "unor documente necesare pentru continuarea procedurii. Vă rugăm să "
                + "prezentați următoarele documente:",
                alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Bullet list of missing documents. ──
            foreach (var doc in missingDocs)
            {
                body.AppendChild(DocxRenderHelpers.Bullet(doc ?? string.Empty));
            }

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Deadline. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Termenul limită pentru prezentare: {DocxRenderHelpers.UtcDateFormat(deadlineUtc)}",
                bold: true));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Examinator CNAS ____________________",
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
