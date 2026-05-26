using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Aviz privind expertiza medicală</em>
/// (individual medical-expert opinion on a beneficiary's case).
/// </summary>
/// <remarks>
/// <para>
/// Captures the opinion of a single named medical expert who has examined a beneficiary
/// and is forwarding their conclusion to the medical commission. This is the
/// expert-evaluator counterpart to <see cref="AvizComisieMedicalaTemplate"/> (which
/// records the commission's collective verdict).
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title.</item>
///   <item>Beneficiary block (IDNP + full name).</item>
///   <item>Expert block (full name + specialty).</item>
///   <item>Evaluation-date line.</item>
///   <item>Expert conclusion (justified paragraph).</item>
///   <item>Optional recommendation paragraph (when supplied).</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>expertFullName</c> — <see cref="string"/>.</item>
///   <item><c>expertSpecialty</c> — <see cref="string"/>.</item>
///   <item><c>evaluationUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>expertConclusion</c> — <see cref="string"/>.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>recommendation</c> — <see cref="string"/>. When present, a separate
///   "Recomandare" paragraph is appended; when absent, the section is skipped.</item>
/// </list>
/// </remarks>
public sealed class AvizExpertizaMedicalaTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "aviz-expertiza-medicala";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "expertFullName", out var expertFullName) || string.IsNullOrWhiteSpace(expertFullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: expertFullName.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "expertSpecialty", out var expertSpecialty) || string.IsNullOrWhiteSpace(expertSpecialty))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: expertSpecialty.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "evaluationUtc", out var evaluationUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: evaluationUtc.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "expertConclusion", out var expertConclusion) || string.IsNullOrWhiteSpace(expertConclusion))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: expertConclusion.");
        }

        // ── Optional recommendation paragraph. ──
        var hasRecommendation = DocxRenderHelpers.TryGet<string>(facts, "recommendation", out var recommendation)
            && !string.IsNullOrWhiteSpace(recommendation);

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("AVIZ PRIVIND EXPERTIZA MEDICALĂ"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Beneficiary block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Beneficiar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Expert block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Expert evaluator:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(expertFullName));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Specialitate: {expertSpecialty}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Evaluation date. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Data evaluării: {DocxRenderHelpers.UtcDateFormat(evaluationUtc)}",
                bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Conclusion (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Concluzia expertului:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(expertConclusion!, alignment: JustificationValues.Both));

            // ── Optional recommendation. ──
            if (hasRecommendation)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
                body.AppendChild(DocxRenderHelpers.Paragraph("Recomandare:", bold: true));
                body.AppendChild(DocxRenderHelpers.Paragraph(recommendation!, alignment: JustificationValues.Both));
            }

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Semnătura expertului: ____________________ ({expertFullName})",
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
