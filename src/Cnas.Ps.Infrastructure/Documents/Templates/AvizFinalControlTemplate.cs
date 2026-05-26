using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Aviz final de control</em>
/// (final audit / control opinion closing a control engagement).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="RaportControlInternTemplate"/> (the interim findings report
/// produced during the control): the <em>aviz final</em> is the closing opinion issued
/// after all observations have been addressed. It documents the engagement period, the
/// overall verdict (positive / with-reserves / negative), and the signing inspector.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Engagement metadata table: subject, period start, period end, verdict.</item>
///   <item>Conclusions paragraph (justified).</item>
///   <item>Optional recommendations bullet list (when supplied).</item>
///   <item>Inspector signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>controlSubject</c> — <see cref="string"/> subject under audit (e.g. dossier id, division name).</item>
///   <item><c>periodStartUtc</c> — <see cref="DateTime"/> (UTC) start of the controlled period.</item>
///   <item><c>periodEndUtc</c> — <see cref="DateTime"/> (UTC) end of the controlled period.</item>
///   <item><c>verdict</c> — <see cref="string"/> overall verdict (e.g. <c>"FAVORABIL"</c>, <c>"CU REZERVE"</c>, <c>"NEFAVORABIL"</c>).</item>
///   <item><c>conclusions</c> — <see cref="string"/> textual summary of the engagement's conclusions.</item>
///   <item><c>inspectorFullName</c> — <see cref="string"/> signing inspector.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>recommendations</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/>
///   of <see cref="string"/>. Each entry is rendered as a bullet under the
///   "Recomandări" section. When absent or empty the section is skipped.</item>
/// </list>
/// </remarks>
public sealed class AvizFinalControlTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "aviz-final-control";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "controlSubject", out var controlSubject) || string.IsNullOrWhiteSpace(controlSubject))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: controlSubject.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "periodStartUtc", out var periodStartUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: periodStartUtc.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "periodEndUtc", out var periodEndUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: periodEndUtc.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "verdict", out var verdict) || string.IsNullOrWhiteSpace(verdict))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: verdict.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "conclusions", out var conclusions) || string.IsNullOrWhiteSpace(conclusions))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: conclusions.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "inspectorFullName", out var inspectorFullName) || string.IsNullOrWhiteSpace(inspectorFullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: inspectorFullName.");
        }

        // ── Optional recommendations list. ──
        // Accept either IReadOnlyList<string> or List<string> to match the convention used
        // by AdeverintaCotizareTemplate's monthlyContributions field. The list is treated
        // as "absent" when null or empty — no exception either way.
        IReadOnlyList<string>? recommendations = null;
        if (DocxRenderHelpers.TryGet<IReadOnlyList<string>>(facts, "recommendations", out var asReadOnly) && asReadOnly is not null)
        {
            recommendations = asReadOnly;
        }
        else if (DocxRenderHelpers.TryGet<List<string>>(facts, "recommendations", out var asList) && asList is not null)
        {
            recommendations = asList;
        }
        var hasRecommendations = recommendations is { Count: > 0 };

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("AVIZ FINAL DE CONTROL"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Engagement metadata table. ──
            var rows = new[]
            {
                new KeyValuePair<string, string>("Subiectul controlului", controlSubject!),
                new KeyValuePair<string, string>(
                    "Începutul perioadei",
                    DocxRenderHelpers.UtcDateFormat(periodStartUtc)),
                new KeyValuePair<string, string>(
                    "Sfârșitul perioadei",
                    DocxRenderHelpers.UtcDateFormat(periodEndUtc)),
                new KeyValuePair<string, string>("Verdict", verdict!),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Conclusions (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Concluzii:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(conclusions, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Optional recommendations bullet list. ──
            if (hasRecommendations)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph("Recomandări:", bold: true));
                foreach (var entry in recommendations!)
                {
                    body.AppendChild(DocxRenderHelpers.Bullet(entry ?? string.Empty));
                }
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            }

            // ── Inspector signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Inspector: ____________________ ({inspectorFullName})",
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
