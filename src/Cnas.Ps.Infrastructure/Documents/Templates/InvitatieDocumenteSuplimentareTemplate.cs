using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Invitație pentru documente suplimentare</em>
/// (invitation for additional documents).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title.</item>
///   <item>Greeting: <c>Stimate/Stimată {fullName}</c>.</item>
///   <item>Body paragraph explaining the request and referencing the dossier id.</item>
///   <item>Bullet list of requested documents.</item>
///   <item>Deadline (UTC date).</item>
///   <item>Signature block.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded — never decode here; CLAUDE.md RULE 3).</item>
///   <item><c>requestedDocuments</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/> of <see cref="string"/>.</item>
///   <item><c>deadlineUtc</c> — <see cref="DateTime"/> (UTC).</item>
/// </list>
/// </remarks>
public sealed class InvitatieDocumenteSuplimentareTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "invitatie-doc-suplimentare";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryFullName", out var fullName) || string.IsNullOrWhiteSpace(fullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryFullName.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "dossierSqid", out var dossierSqid) || string.IsNullOrWhiteSpace(dossierSqid))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: dossierSqid.");
        }

        IReadOnlyList<string>? requestedDocuments = null;
        if (facts.TryGetValue("requestedDocuments", out var rawDocs))
        {
            requestedDocuments = rawDocs switch
            {
                IReadOnlyList<string> roList => roList,
                IEnumerable<string> en => en.ToList(),
                _ => null,
            };
        }

        if (requestedDocuments is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: requestedDocuments.");
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

            body.AppendChild(DocxRenderHelpers.Heading("INVITAȚIE PENTRU DOCUMENTE SUPLIMENTARE"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Greeting. ──
            body.AppendChild(DocxRenderHelpers.Paragraph($"Stimate/Stimată {fullName},", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Body. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"În legătură cu dosarul Dvs. cu identificatorul {dossierSqid}, vă rugăm "
                + "să prezentați următoarele documente suplimentare:",
                alignment: JustificationValues.Both));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Bullet list. ──
            foreach (var doc in requestedDocuments)
            {
                body.AppendChild(DocxRenderHelpers.Bullet(doc ?? string.Empty));
            }

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Deadline. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Termenul limită: {DocxRenderHelpers.UtcDateFormat(deadlineUtc)}",
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

            mainPart.Document = new WordDocument(body);
            mainPart.Document.Save();
        }

        return Result<byte[]>.Success(ms.ToArray());
    }
}
