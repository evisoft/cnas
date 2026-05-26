using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Adresă certificat</em> (generic certificate-letter shell
/// used for ad-hoc certificates that do not fit a more specific template).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (full name + postal address).</item>
///   <item>Subject line (italic).</item>
///   <item>Body paragraph (justified).</item>
///   <item>Signature block — explicit signatory name (no placeholder).</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>recipientFullName</c> — <see cref="string"/>.</item>
///   <item><c>recipientAddress</c> — <see cref="string"/>.</item>
///   <item><c>subject</c> — <see cref="string"/>.</item>
///   <item><c>bodyText</c> — <see cref="string"/>.</item>
///   <item><c>signatoryName</c> — <see cref="string"/>.</item>
/// </list>
/// </remarks>
public sealed class AdresaCertificatTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "adresa-certificat";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "subject", out var subject) || string.IsNullOrWhiteSpace(subject))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: subject.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "bodyText", out var bodyText) || string.IsNullOrWhiteSpace(bodyText))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: bodyText.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "signatoryName", out var signatoryName) || string.IsNullOrWhiteSpace(signatoryName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: signatoryName.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("ADRESĂ CERTIFICAT"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(fullName));
            body.AppendChild(DocxRenderHelpers.Paragraph(address));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Subject line (italic). ──
            body.AppendChild(DocxRenderHelpers.Paragraph($"Subiect: {subject}", italic: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Body (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(bodyText, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block (explicit signatory name). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Semnătura: {signatoryName}",
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
