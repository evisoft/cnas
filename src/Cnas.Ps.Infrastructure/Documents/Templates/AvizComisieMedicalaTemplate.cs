using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Aviz comisie medicală</em> (medical-commission opinion).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Patient block (IDNP + full name + date of birth, UTC date).</item>
///   <item>Diagnosis section — italic justified paragraph carrying the diagnosis text.</item>
///   <item>Commission verdict — justified paragraph.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>patientIdnp</c> — <see cref="string"/>.</item>
///   <item><c>patientFullName</c> — <see cref="string"/>.</item>
///   <item><c>dateOfBirthUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>diagnosis</c> — <see cref="string"/>.</item>
///   <item><c>verdict</c> — <see cref="string"/>.</item>
/// </list>
/// </remarks>
public sealed class AvizComisieMedicalaTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "aviz-comisie-medicala";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "patientIdnp", out var idnp) || string.IsNullOrWhiteSpace(idnp))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: patientIdnp.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "patientFullName", out var fullName) || string.IsNullOrWhiteSpace(fullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: patientFullName.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "dateOfBirthUtc", out var dateOfBirthUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: dateOfBirthUtc.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "diagnosis", out var diagnosis) || string.IsNullOrWhiteSpace(diagnosis))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: diagnosis.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "verdict", out var verdict) || string.IsNullOrWhiteSpace(verdict))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: verdict.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("AVIZ COMISIE MEDICALĂ"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Patient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Pacient:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Data nașterii: {DocxRenderHelpers.UtcDateFormat(dateOfBirthUtc)}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Diagnosis (italic, justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Diagnostic:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                diagnosis,
                italic: true,
                alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Verdict (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Avizul comisiei:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(verdict, alignment: JustificationValues.Both));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Președintele comisiei ____________________",
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
