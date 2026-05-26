using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Decizie de suspendare a plății</em>
/// (payment-suspension decision).
/// </summary>
/// <remarks>
/// <para>
/// Issued when CNAS suspends an active benefit stream (e.g. expired medical certificate,
/// failure to confirm residency, suspected fraud under investigation). Distinct from the
/// existing <see cref="DispozitieReluareTemplate"/> (resumption disposition) and from
/// <see cref="DecizieRevocareTemplate"/> (full revocation): suspension is reversible and
/// typically carries a review-after date.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (IDNP, full name, dossier id).</item>
///   <item>Suspension reason (justified paragraph).</item>
///   <item>Effective-from date line (bold).</item>
///   <item>Optional review-after date line (when supplied).</item>
///   <item>Appeal-rights notice in italic small font.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>suspensionReason</c> — <see cref="string"/>.</item>
///   <item><c>effectiveFromUtc</c> — <see cref="DateTime"/> (UTC).</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>reviewAfterUtc</c> — <see cref="DateTime"/> (UTC). When present, a "review after"
///   line is rendered; when omitted, the line is skipped (no exception).</item>
/// </list>
/// </remarks>
public sealed class DecizieSuspendarePlataTemplate : IDocxTemplate
{
    /// <summary>
    /// Stable template code (wire-format identifier). Kept as a public constant on the
    /// concrete template so callers can reference it without touching the interface.
    /// </summary>
    public const string Code = "decizie-suspendare-plata";

    /// <summary>
    /// Romanian appeal-rights notice rendered as italic small text at the bottom of every
    /// suspension decision. Held centrally so legal can review it in one place.
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

        if (!DocxRenderHelpers.TryGet<string>(facts, "suspensionReason", out var suspensionReason) || string.IsNullOrWhiteSpace(suspensionReason))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: suspensionReason.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "effectiveFromUtc", out var effectiveFromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: effectiveFromUtc.");
        }

        // ── Optional review-after-date. ──
        var hasReviewAfter = DocxRenderHelpers.TryGet<DateTime>(facts, "reviewAfterUtc", out var reviewAfterUtc);

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("DECIZIE DE SUSPENDARE A PLĂȚII"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Reason (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Motivul suspendării:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(suspensionReason, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Effective-from date (bold). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Plata se suspendă începând cu data de {DocxRenderHelpers.UtcDateFormat(effectiveFromUtc)}.",
                bold: true));

            // ── Optional review-after date. ──
            if (hasReviewAfter)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"Reexaminarea cazului este programată după data de {DocxRenderHelpers.UtcDateFormat(reviewAfterUtc)}."));
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
