using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Raport de control intern</em> (internal-audit report).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Audit-period line (<c>from</c> – <c>to</c>, UTC dates).</item>
///   <item>Auditor block (full name).</item>
///   <item>Findings 2-column table — left "Constatare", right "Severitate".</item>
///   <item>Recommendations bullet list.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>fromUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>toUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>auditorFullName</c> — <see cref="string"/>.</item>
///   <item><c>findings</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/> of <see cref="Finding"/>.</item>
///   <item><c>recommendations</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/> of <see cref="string"/>.</item>
/// </list>
/// </remarks>
public sealed class RaportControlInternTemplate : IDocxTemplate
{
    /// <summary>
    /// One row in the findings table.
    /// </summary>
    /// <param name="Title">Short description of the finding (left column).</param>
    /// <param name="Severity">
    /// Severity label (right column). Free-form Romanian string — typical values:
    /// <c>"Joasă"</c>, <c>"Medie"</c>, <c>"Ridicată"</c>, <c>"Critică"</c>.
    /// </param>
    public sealed record Finding(string Title, string Severity);

    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "raport-control-intern";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "fromUtc", out var fromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: fromUtc.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "toUtc", out var toUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: toUtc.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "auditorFullName", out var auditor) || string.IsNullOrWhiteSpace(auditor))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: auditorFullName.");
        }

        // Accept both List<Finding> and IReadOnlyList<Finding> at the boundary.
        IReadOnlyList<Finding>? findings = null;
        if (facts.TryGetValue("findings", out var rawFindings))
        {
            findings = rawFindings switch
            {
                IReadOnlyList<Finding> roList => roList,
                IEnumerable<Finding> en => en.ToList(),
                _ => null,
            };
        }

        if (findings is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: findings.");
        }

        IReadOnlyList<string>? recommendations = null;
        if (facts.TryGetValue("recommendations", out var rawRecs))
        {
            recommendations = rawRecs switch
            {
                IReadOnlyList<string> roList => roList,
                IEnumerable<string> en => en.ToList(),
                _ => null,
            };
        }

        if (recommendations is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: recommendations.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("RAPORT DE CONTROL INTERN"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Period. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Perioada controlului: {DocxRenderHelpers.UtcDateFormat(fromUtc)} — "
                + DocxRenderHelpers.UtcDateFormat(toUtc),
                bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Auditor block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Auditor:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(auditor));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Findings table (header row + one row per finding). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Constatări:", bold: true));
            var rows = new List<KeyValuePair<string, string>>(findings.Count + 1)
            {
                // Header row.
                new("Constatare", "Severitate"),
            };
            foreach (var f in findings)
            {
                rows.Add(new KeyValuePair<string, string>(f.Title ?? string.Empty, f.Severity ?? string.Empty));
            }
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recommendations bullet list. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Recomandări:", bold: true));
            foreach (var rec in recommendations)
            {
                body.AppendChild(DocxRenderHelpers.Bullet(rec ?? string.Empty));
            }

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
