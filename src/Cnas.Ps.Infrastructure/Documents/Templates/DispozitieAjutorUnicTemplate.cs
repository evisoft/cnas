using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Dispoziție privind acordarea ajutorului unic</em>
/// (one-time / extraordinary aid disposition).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the recurring-pension <see cref="DispozitiaInscriereTemplate"/> and from
/// the monthly <see cref="DispozitieRecalculTemplate"/>: an "ajutor unic" is a
/// single-disbursement aid (e.g. a hardship grant, a funeral assistance, an exceptional
/// allowance) approved by special decision. The dispoziție records the lump-sum amount
/// in MDL, the disbursement date, and the legal ground under which the aid is granted.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (IDNP, full name, dossier id).</item>
///   <item>Legal-ground paragraph (justified).</item>
///   <item>Key/value table: amount (MDL), disbursement date.</item>
///   <item>Optional payment-method line (when supplied).</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>legalGround</c> — <see cref="string"/> textual description of the legal basis.</item>
///   <item><c>aidAmountMdl</c> — <see cref="decimal"/> aid amount in MDL.</item>
///   <item><c>disbursementUtc</c> — <see cref="DateTime"/> (UTC) planned disbursement date.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>paymentMethod</c> — <see cref="string"/>. Free-form description of the payment
///   channel (e.g. <c>"transfer bancar pe contul indicat în cerere"</c>); when absent the
///   payment-method line is omitted (no exception).</item>
/// </list>
/// </remarks>
public sealed class DispozitieAjutorUnicTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "dispozitie-ajutor-unic";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "legalGround", out var legalGround) || string.IsNullOrWhiteSpace(legalGround))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: legalGround.");
        }

        if (!DocxRenderHelpers.TryGet<decimal>(facts, "aidAmountMdl", out var aidAmountMdl))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: aidAmountMdl.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "disbursementUtc", out var disbursementUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: disbursementUtc.");
        }

        // ── Optional payment method. ──
        var hasPaymentMethod = DocxRenderHelpers.TryGet<string>(facts, "paymentMethod", out var paymentMethod)
            && !string.IsNullOrWhiteSpace(paymentMethod);

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("DISPOZIȚIE — ACORDARE AJUTOR UNIC"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Beneficiar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Legal ground (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Temei legal:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(legalGround, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Two-row amount/date table. ──
            var rows = new[]
            {
                new KeyValuePair<string, string>(
                    "Sumă acordată",
                    DocxRenderHelpers.MoneyFormat(aidAmountMdl)),
                new KeyValuePair<string, string>(
                    "Data plății",
                    DocxRenderHelpers.UtcDateFormat(disbursementUtc)),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Optional payment method. ──
            if (hasPaymentMethod)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"Modalitate de plată: {paymentMethod}"));
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            }

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
