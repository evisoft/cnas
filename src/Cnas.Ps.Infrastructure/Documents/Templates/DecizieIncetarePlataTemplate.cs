using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Decizie de încetare a plății</em>
/// (final cessation-of-payment decision).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="DecizieSuspendarePlataTemplate"/> (reversible suspension):
/// the încetare is the final, non-reversible discontinuation of a benefit stream
/// (e.g. death of the beneficiary, loss of eligibility, conviction triggering forfeiture).
/// It states the legal ground (codified article) alongside the human-readable reason.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (IDNP, full name, dossier id).</item>
///   <item>Cessation-reason paragraph (justified).</item>
///   <item>Legal-ground paragraph (justified, italic).</item>
///   <item>Effective-from date line (bold).</item>
///   <item>Optional final-disbursement-date line (when supplied).</item>
///   <item>Appeal-rights notice in italic small font.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>cessationReason</c> — <see cref="string"/> human-readable reason.</item>
///   <item><c>legalGround</c> — <see cref="string"/> codified legal reference.</item>
///   <item><c>effectiveFromUtc</c> — <see cref="DateTime"/> (UTC) cessation effective date.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>finalDisbursementDateUtc</c> — <see cref="DateTime"/> (UTC). When present,
///   a "ultima plată efectuată" line is rendered; when omitted, the line is skipped.</item>
/// </list>
/// </remarks>
public sealed class DecizieIncetarePlataTemplate : IDocxTemplate
{
    /// <summary>
    /// Stable template code (wire-format identifier). Kept as a public constant on the
    /// concrete template so callers can reference it without touching the interface.
    /// </summary>
    public const string Code = "decizie-incetare-plata";

    /// <summary>
    /// Romanian appeal-rights notice rendered as italic small text at the bottom of every
    /// cessation decision. Held centrally so legal can review it in one place.
    /// </summary>
    private const string AppealRightsNoticeRo =
        "Prezenta decizie poate fi contestată în termen de 30 zile de la data comunicării, "
        + "în conformitate cu legislația privind contenciosul administrativ al Republicii Moldova.";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "cessationReason", out var cessationReason) || string.IsNullOrWhiteSpace(cessationReason))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: cessationReason.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "legalGround", out var legalGround) || string.IsNullOrWhiteSpace(legalGround))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: legalGround.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "effectiveFromUtc", out var effectiveFromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: effectiveFromUtc.");
        }

        // ── Optional final-disbursement-date. ──
        var hasFinalDisbursement = DocxRenderHelpers.TryGet<DateTime>(facts, "finalDisbursementDateUtc", out var finalDisbursementDateUtc);

        // MemoryStream lifetime nesting: keep `ms` open across `ms.ToArray()` by closing
        // the package first (the inner using). A top-level using would dispose the stream
        // before ToArray() returns.
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("DECIZIE DE ÎNCETARE A PLĂȚII"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Reason (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Motivul încetării:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(cessationReason!, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Legal ground (italic, justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Temei legal:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(legalGround!, italic: true, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Effective-from date (bold). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Plata încetează începând cu data de {DocxRenderHelpers.UtcDateFormat(effectiveFromUtc)}.",
                bold: true));

            // ── Optional final-disbursement-date. ──
            if (hasFinalDisbursement)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"Ultima plată efectuată: {DocxRenderHelpers.UtcDateFormat(finalDisbursementDateUtc)}."));
            }

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Appeal-rights notice. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                AppealRightsNoticeRo,
                italic: true,
                fontSizeHalfPoints: "16",
                alignment: JustificationValues.Both));
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
