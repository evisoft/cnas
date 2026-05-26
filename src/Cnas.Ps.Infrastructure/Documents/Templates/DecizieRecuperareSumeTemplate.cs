using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Decizie de recuperare a sumelor plătite necuvenit</em>
/// (over-payment recovery decision).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="DecizieAjustareSumeTemplate"/> (one-off correction of a
/// single past disbursement): the recuperare decision establishes a recovery plan for
/// a sum that the beneficiary received but was not entitled to. The decision states
/// the over-paid amount, the reason for the over-payment, the recovery method
/// (voluntary repayment, monthly garnishment from current benefits, court action), and
/// a repayment deadline. An optional bank IBAN is rendered when voluntary repayment
/// is offered.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (IDNP, full name, dossier id).</item>
///   <item>Over-payment-reason paragraph (justified).</item>
///   <item>Key/value table: over-paid amount, recovery method, repayment deadline.</item>
///   <item>Optional bank IBAN line (when supplied).</item>
///   <item>Appeal-rights notice in italic small font.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>overpaidAmountMdl</c> — <see cref="decimal"/> over-paid amount in MDL.</item>
///   <item><c>overpaymentReason</c> — <see cref="string"/> why the over-payment occurred.</item>
///   <item><c>recoveryMethod</c> — <see cref="string"/> how the sum will be recovered.</item>
///   <item><c>repaymentDeadlineUtc</c> — <see cref="DateTime"/> (UTC) deadline for repayment.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>bankIban</c> — <see cref="string"/>. When present, a "Cont bancar" line is
///   rendered so the beneficiary knows where to deposit voluntary repayment; when
///   omitted the line is skipped (no exception).</item>
/// </list>
/// </remarks>
public sealed class DecizieRecuperareSumeTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "decizie-recuperare-sume";

    /// <summary>
    /// Romanian appeal-rights notice rendered as italic small text at the bottom of every
    /// recovery decision. Held centrally so legal can review it in one place.
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

        if (!DocxRenderHelpers.TryGet<decimal>(facts, "overpaidAmountMdl", out var overpaidAmountMdl))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: overpaidAmountMdl.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "overpaymentReason", out var overpaymentReason) || string.IsNullOrWhiteSpace(overpaymentReason))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: overpaymentReason.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "recoveryMethod", out var recoveryMethod) || string.IsNullOrWhiteSpace(recoveryMethod))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: recoveryMethod.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "repaymentDeadlineUtc", out var repaymentDeadlineUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: repaymentDeadlineUtc.");
        }

        // ── Optional bank IBAN. ──
        var hasBankIban = DocxRenderHelpers.TryGet<string>(facts, "bankIban", out var bankIban)
            && !string.IsNullOrWhiteSpace(bankIban);

        // MemoryStream lifetime nesting: keep `ms` open across `ms.ToArray()` by closing
        // the package first (the inner using).
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("DECIZIE DE RECUPERARE A SUMELOR"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Over-payment reason (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Motivul plății necuvenite:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(overpaymentReason!, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recovery detail table. ──
            var rows = new[]
            {
                new KeyValuePair<string, string>(
                    "Sumă plătită necuvenit",
                    DocxRenderHelpers.MoneyFormat(overpaidAmountMdl)),
                new KeyValuePair<string, string>("Modalitatea de recuperare", recoveryMethod!),
                new KeyValuePair<string, string>(
                    "Termen de restituire",
                    DocxRenderHelpers.UtcDateFormat(repaymentDeadlineUtc)),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Optional bank IBAN. ──
            if (hasBankIban)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"Cont bancar pentru restituire voluntară: {bankIban}",
                    bold: true));
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            }

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
