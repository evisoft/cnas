using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// R2000 / Annex 7 §8.7.1 — <em>Cerere înregistrare persoană asigurată</em>
/// (request to register a new insured person on the contributor's ledger).
/// </summary>
/// <remarks>
/// <para>
/// Standard <em>Cerere</em> template per the service code that handles new
/// insured-person registration (TOR §8.2.1). The template carries the
/// solicitant's identity block, the proposed insured-person's identity
/// block, the relationship between them, and the statutory acknowledgement
/// notice.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold).</item>
///   <item>Solicitant block (full name, IDNP).</item>
///   <item>Insured-person block (full name, IDNP, date of birth).</item>
///   <item>Relationship paragraph (free-form, justified).</item>
///   <item>Statutory acknowledgement paragraph (italic).</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>solicitantFullName</c> — <see cref="string"/>.</item>
///   <item><c>solicitantIdnp</c> — <see cref="string"/>.</item>
///   <item><c>insuredFullName</c> — <see cref="string"/>.</item>
///   <item><c>insuredIdnp</c> — <see cref="string"/>.</item>
///   <item><c>insuredDobUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>relationship</c> — <see cref="string"/>.</item>
/// </list>
/// </remarks>
public sealed class CerereInregistrareInsuredPersonTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "cerere-inregistrare-insured-person";

    /// <summary>
    /// Statutory acknowledgement paragraph rendered in italic near the bottom
    /// of every registration request. Held centrally so legal can review it
    /// in one place.
    /// </summary>
    private const string AcknowledgementNoticeRo =
        "Subsemnatul declar pe propria răspundere că datele furnizate sunt "
        + "corecte și complete. Cunosc faptul că furnizarea de date false "
        + "constituie infracțiune conform legislației Republicii Moldova.";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "solicitantFullName", out var solicitantName) || string.IsNullOrWhiteSpace(solicitantName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: solicitantFullName.");
        }
        if (!DocxRenderHelpers.TryGet<string>(facts, "solicitantIdnp", out var solicitantIdnp) || string.IsNullOrWhiteSpace(solicitantIdnp))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: solicitantIdnp.");
        }
        if (!DocxRenderHelpers.TryGet<string>(facts, "insuredFullName", out var insuredName) || string.IsNullOrWhiteSpace(insuredName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: insuredFullName.");
        }
        if (!DocxRenderHelpers.TryGet<string>(facts, "insuredIdnp", out var insuredIdnp) || string.IsNullOrWhiteSpace(insuredIdnp))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: insuredIdnp.");
        }
        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "insuredDobUtc", out var insuredDob))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: insuredDobUtc.");
        }
        if (!DocxRenderHelpers.TryGet<string>(facts, "relationship", out var relationship) || string.IsNullOrWhiteSpace(relationship))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: relationship.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("CERERE ÎNREGISTRARE PERSOANĂ ASIGURATĂ"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Solicitant block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Solicitant:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {solicitantName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {solicitantIdnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Insured-person block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Persoană asigurată propusă:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {insuredName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {insuredIdnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Data nașterii: {DocxRenderHelpers.UtcDateFormat(insuredDob)}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Relationship. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Relația cu solicitantul:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(relationship, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Statutory acknowledgement. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                AcknowledgementNoticeRo,
                italic: true,
                fontSizeHalfPoints: "18",
                alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Semnătura solicitantului ____________________",
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
