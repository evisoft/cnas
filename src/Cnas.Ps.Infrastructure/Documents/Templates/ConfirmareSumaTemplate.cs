using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Confirmarea sumei</em> (confirmation of a specific MDL amount).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Beneficiary block (IDNP + full name).</item>
///   <item>Amount in bold money format (<c>"#,##0.00 MDL"</c>).</item>
///   <item>Period line (<c>fromUtc</c> – <c>toUtc</c>, UTC dates).</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>amountMdl</c> — <see cref="decimal"/>.</item>
///   <item><c>fromUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>toUtc</c> — <see cref="DateTime"/> (UTC).</item>
/// </list>
/// </remarks>
public sealed class ConfirmareSumaTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "confirmare-suma";

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

        if (!DocxRenderHelpers.TryGet<decimal>(facts, "amountMdl", out var amountMdl))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: amountMdl.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "fromUtc", out var fromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: fromUtc.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "toUtc", out var toUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: toUtc.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("CONFIRMAREA SUMEI"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Beneficiary block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Beneficiar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Amount (bold money format). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Suma confirmată: {DocxRenderHelpers.MoneyFormat(amountMdl)}",
                bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Period (UTC dates). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Perioada: {DocxRenderHelpers.UtcDateFormat(fromUtc)} — "
                + DocxRenderHelpers.UtcDateFormat(toUtc)));

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
