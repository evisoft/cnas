using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Cerere de recalcul a plății</em>
/// (formal acknowledgment of a payment-recalculation request).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="DispozitieRecalculTemplate"/>: the dispoziție carries the
/// recalculation outcome (before/after amounts) after examination; this template is the
/// formal acknowledgement issued to the beneficiary when CNAS <em>receives</em> the
/// recalculation request — it confirms what was requested, when it was received, and the
/// statutory deadline by which CNAS will respond.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title.</item>
///   <item>Recipient block (IDNP, full name, dossier id).</item>
///   <item>Request reason (justified paragraph).</item>
///   <item>2-row key/value table: request-received-on, response-by deadline.</item>
///   <item>Optional attachments-list paragraph (when supplied).</item>
///   <item>Statutory-deadline notice in italic.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>requestReason</c> — <see cref="string"/>.</item>
///   <item><c>requestUtc</c> — <see cref="DateTime"/> (UTC) — when the request was received.</item>
///   <item><c>responseByUtc</c> — <see cref="DateTime"/> (UTC) — statutory response deadline.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>attachmentsList</c> — <see cref="string"/>. Free-form enumeration of documents
///   submitted alongside the request; when absent, the attachments section is skipped.</item>
/// </list>
/// </remarks>
public sealed class CerereRecalculPlataTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "cerere-recalcul-plata";

    /// <summary>
    /// Statutory-deadline notice rendered as italic text near the bottom of every
    /// recalculation acknowledgement. Held centrally so legal can review it in one place.
    /// </summary>
    private const string StatutoryDeadlineNoticeRo =
        "În conformitate cu legislația privind asigurările sociale de stat, "
        + "CNAS va comunica rezultatul examinării în termenul indicat mai sus.";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "dossierSqid", out var dossierSqid) || string.IsNullOrWhiteSpace(dossierSqid))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: dossierSqid.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "requestReason", out var requestReason) || string.IsNullOrWhiteSpace(requestReason))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: requestReason.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "requestUtc", out var requestUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: requestUtc.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "responseByUtc", out var responseByUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: responseByUtc.");
        }

        // ── Optional attachments paragraph. ──
        var hasAttachments = DocxRenderHelpers.TryGet<string>(facts, "attachmentsList", out var attachmentsList)
            && !string.IsNullOrWhiteSpace(attachmentsList);

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("CERERE DE RECALCUL A PLĂȚII"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Solicitant:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Request reason (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Motivul cererii de recalcul:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(requestReason, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Two-row dates table. ──
            var rows = new[]
            {
                new KeyValuePair<string, string>(
                    "Data primirii cererii",
                    DocxRenderHelpers.UtcDateFormat(requestUtc)),
                new KeyValuePair<string, string>(
                    "Termen de răspuns",
                    DocxRenderHelpers.UtcDateFormat(responseByUtc)),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Optional attachments. ──
            if (hasAttachments)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph("Documente anexate:", bold: true));
                body.AppendChild(DocxRenderHelpers.Paragraph(attachmentsList!, alignment: JustificationValues.Both));
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            }

            // ── Statutory deadline notice. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                StatutoryDeadlineNoticeRo,
                italic: true,
                fontSizeHalfPoints: "18",
                alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Funcționar CNAS ____________________",
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
