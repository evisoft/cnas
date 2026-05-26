using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Decizie de ajustare a sumei</em>
/// (decision correcting a previously-paid amount).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="DispozitieRecalculTemplate"/> (forward-looking recalculation
/// of monthly amounts): the ajustare carries a one-off correction of a specific past
/// disbursement — either an under-payment that is being topped up, or an over-payment
/// that the beneficiary is asked to return. The decision states the originally-paid
/// amount, the corrected amount, and the resulting delta (signed).
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (IDNP, full name, dossier id).</item>
///   <item>Correction-reason paragraph (justified).</item>
///   <item>Key/value table: original amount, corrected amount, delta.</item>
///   <item>Optional reference-payment-id line (when supplied).</item>
///   <item>Appeal-rights notice in italic small font.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>correctionReason</c> — <see cref="string"/>.</item>
///   <item><c>originalAmountMdl</c> — <see cref="decimal"/> originally-paid amount in MDL.</item>
///   <item><c>correctedAmountMdl</c> — <see cref="decimal"/> corrected amount in MDL.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>referencePaymentSqid</c> — <see cref="string"/>. Sqid-encoded reference to the
///   original payment record; when present, a "Plată referință" line is rendered.</item>
/// </list>
/// <para>
/// The delta is computed in-template as <c>correctedAmountMdl - originalAmountMdl</c> and
/// rendered with an explicit sign ("+1,234.50 MDL" or "-200.00 MDL") so the recipient can
/// see at a glance whether they owe a return or are due a top-up.
/// </para>
/// </remarks>
public sealed class DecizieAjustareSumeTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "decizie-ajustare-sume";

    /// <summary>Romanian appeal-rights notice rendered at the bottom of every adjustment decision.</summary>
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

        if (!DocxRenderHelpers.TryGet<string>(facts, "correctionReason", out var correctionReason) || string.IsNullOrWhiteSpace(correctionReason))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: correctionReason.");
        }

        if (!DocxRenderHelpers.TryGet<decimal>(facts, "originalAmountMdl", out var originalAmountMdl))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: originalAmountMdl.");
        }

        if (!DocxRenderHelpers.TryGet<decimal>(facts, "correctedAmountMdl", out var correctedAmountMdl))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: correctedAmountMdl.");
        }

        // ── Optional reference payment id. ──
        var hasReferencePayment = DocxRenderHelpers.TryGet<string>(facts, "referencePaymentSqid", out var referencePaymentSqid)
            && !string.IsNullOrWhiteSpace(referencePaymentSqid);

        // Compute the signed delta in-template so the recipient sees the magnitude and
        // direction at a glance. Positive = top-up owed by CNAS; negative = return owed by
        // the beneficiary.
        var delta = correctedAmountMdl - originalAmountMdl;
        var deltaSign = delta >= 0 ? "+" : "-";
        var deltaFormatted = $"{deltaSign}{DocxRenderHelpers.MoneyFormat(Math.Abs(delta))}";

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("DECIZIE DE AJUSTARE A SUMEI"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            if (hasReferencePayment)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph($"Plată referință: {referencePaymentSqid}"));
            }

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Reason (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Motivul ajustării:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(correctionReason, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Amounts table. ──
            var rows = new[]
            {
                new KeyValuePair<string, string>(
                    "Sumă inițială plătită",
                    DocxRenderHelpers.MoneyFormat(originalAmountMdl)),
                new KeyValuePair<string, string>(
                    "Sumă corectată",
                    DocxRenderHelpers.MoneyFormat(correctedAmountMdl)),
                new KeyValuePair<string, string>("Diferență", deltaFormatted),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));
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
