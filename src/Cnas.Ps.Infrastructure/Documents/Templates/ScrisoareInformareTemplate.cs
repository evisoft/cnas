using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Scrisoare de informare</em> (informational letter).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (full name + postal address).</item>
///   <item>Informational body paragraph (justified).</item>
///   <item>Date footer — informational letters carry only a generation-date footer; there
///   is no signature line per the requirements.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>recipientFullName</c> — <see cref="string"/>.</item>
///   <item><c>recipientAddress</c> — <see cref="string"/>.</item>
///   <item><c>informationText</c> — <see cref="string"/>.</item>
/// </list>
/// </remarks>
public sealed class ScrisoareInformareTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "scrisoare-informare";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "recipientFullName", out var fullName) || string.IsNullOrWhiteSpace(fullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: recipientFullName.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "recipientAddress", out var address) || string.IsNullOrWhiteSpace(address))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: recipientAddress.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "informationText", out var informationText) || string.IsNullOrWhiteSpace(informationText))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: informationText.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("SCRISOARE DE INFORMARE"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(fullName));
            body.AppendChild(DocxRenderHelpers.Paragraph(address));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Informational body (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(informationText, alignment: JustificationValues.Both));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Date footer — informational letters intentionally omit the signature line. ──
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
