using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Notă de informare</em>
/// (internal informational note attached to a dossier).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="ScrisoareInformareTemplate"/> (citizen-facing letter): this
/// template produces an <em>internal</em> note authored by an examiner or supervisor,
/// stamped with their full name and role, and attached to the dossier as part of the
/// audit trail. There is no citizen recipient block.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Dossier identifier line.</item>
///   <item>Author block (full name + role + authored-on date).</item>
///   <item>Optional reference-code line (when supplied).</item>
///   <item>Note content (justified paragraph).</item>
///   <item>Author signature line.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>authorFullName</c> — <see cref="string"/>.</item>
///   <item><c>authorRole</c> — <see cref="string"/>.</item>
///   <item><c>authoredUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>noteContent</c> — <see cref="string"/>.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>referenceCode</c> — <see cref="string"/>. Internal correlation identifier
///   (e.g. <c>"NOTA-2026-0042"</c>); rendered as an additional metadata line when present.</item>
/// </list>
/// </remarks>
public sealed class NotaInformareTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "nota-informare";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "authorFullName", out var authorFullName) || string.IsNullOrWhiteSpace(authorFullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: authorFullName.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "authorRole", out var authorRole) || string.IsNullOrWhiteSpace(authorRole))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: authorRole.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "authoredUtc", out var authoredUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: authoredUtc.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "noteContent", out var noteContent) || string.IsNullOrWhiteSpace(noteContent))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: noteContent.");
        }

        // ── Optional reference code. ──
        var hasReferenceCode = DocxRenderHelpers.TryGet<string>(facts, "referenceCode", out var referenceCode)
            && !string.IsNullOrWhiteSpace(referenceCode);

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("NOTĂ DE INFORMARE"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Dossier identifier. ──
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}", bold: true));
            if (hasReferenceCode)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph($"Cod referință: {referenceCode}"));
            }

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Author block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Autor:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {authorFullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Funcție: {authorRole}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Data întocmirii: {DocxRenderHelpers.UtcDateFormat(authoredUtc)}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Note content (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Conținut:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(noteContent, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Author signature line. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Semnătura autorului: ____________________ ({authorFullName})",
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
