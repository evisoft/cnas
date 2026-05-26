using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// R2002 / Annex 7 §8.7.3 — <em>Raport statistică plăți</em>
/// (statistical report of payment volumes per service category over a
/// reporting period).
/// </summary>
/// <remarks>
/// <para>
/// Standard <em>Raport</em> template per criteria 8.7.3.2 (statistical
/// aggregates). Distinct from <see cref="RaportControlInternTemplate"/> —
/// this template is the printable wrapper around the <c>RPT-TOTAL-PAYMENTS-PER-MONTH</c>
/// / <c>RPT-DAILY-CASH-FLOW</c> family. It carries the audit-period header,
/// the per-category breakdown table, and the running total.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold).</item>
///   <item>Audit-period line (<c>fromUtc</c> – <c>toUtc</c>, UTC dates).</item>
///   <item>Two-column table: Category → Total MDL.</item>
///   <item>Total row at the bottom.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>fromUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>toUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>rows</c> — <see cref="IReadOnlyList{T}"/> of <see cref="PaymentRow"/>.</item>
/// </list>
/// </remarks>
public sealed class RaportStatisticaPlatiTemplate : IDocxTemplate
{
    /// <summary>
    /// One row in the per-category aggregate table.
    /// </summary>
    /// <param name="Category">Free-form Romanian label for the service category.</param>
    /// <param name="TotalMdl">Aggregate MDL paid in the row's category, ≥ 0.</param>
    public sealed record PaymentRow(string Category, decimal TotalMdl);

    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "raport-statistica-plati";

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

        IReadOnlyList<PaymentRow>? rows = null;
        if (facts.TryGetValue("rows", out var raw))
        {
            rows = raw switch
            {
                IReadOnlyList<PaymentRow> roList => roList,
                IEnumerable<PaymentRow> en => en.ToList(),
                _ => null,
            };
        }
        if (rows is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: rows.");
        }

        // Compute total in invariant culture.
        var total = rows.Sum(r => r.TotalMdl);

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("RAPORT STATISTICĂ PLĂȚI"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Period. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Perioada raportului: {DocxRenderHelpers.UtcDateFormat(fromUtc)} — "
                + DocxRenderHelpers.UtcDateFormat(toUtc),
                bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Per-category table. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Plăți pe categorie:", bold: true));
            var tableRows = new List<KeyValuePair<string, string>>(rows.Count + 2)
            {
                // Header.
                new("Categorie", "Suma totală (MDL)"),
            };
            foreach (var r in rows)
            {
                tableRows.Add(new KeyValuePair<string, string>(
                    r.Category ?? string.Empty,
                    r.TotalMdl.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
            }
            // Footer total row.
            tableRows.Add(new KeyValuePair<string, string>(
                "TOTAL",
                total.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
            body.AppendChild(DocxRenderHelpers.KeyValueTable(tableRows));

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
