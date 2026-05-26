using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Adeverință de cotizare</em> (contribution certificate).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Beneficiary block (IDNP + full name).</item>
///   <item>Contribution-period line (from – to, UTC dates).</item>
///   <item>2-column key/value table — month label (left) × contribution amount in MDL (right).
///   The first row is a header (<c>"Luna"</c> / <c>"Cuantum cotizare"</c>).</item>
///   <item>Total amount in bold.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>fromUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>toUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>monthlyContributions</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/> of <see cref="MonthlyContribution"/>.</item>
///   <item><c>totalAmountMdl</c> — <see cref="decimal"/>.</item>
/// </list>
/// </remarks>
public sealed class AdeverintaCotizareTemplate : IDocxTemplate
{
    /// <summary>
    /// One row in the monthly-contributions table.
    /// </summary>
    /// <param name="Month">
    /// Month label (left column). Free-form short string, typically ISO year-month
    /// (<c>"2026-01"</c>) or the Romanian month name (<c>"Ianuarie 2026"</c>).
    /// </param>
    /// <param name="AmountMdl">
    /// Contribution amount for the month in MDL. Formatted with
    /// <see cref="DocxRenderHelpers.MoneyFormat"/> for stable invariant-culture output.
    /// </param>
    public sealed record MonthlyContribution(string Month, decimal AmountMdl);

    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "adeverinta-cotizare";

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

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "fromUtc", out var fromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: fromUtc.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "toUtc", out var toUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: toUtc.");
        }

        // Accept both List<MonthlyContribution> and IReadOnlyList<MonthlyContribution>.
        IReadOnlyList<MonthlyContribution>? contributions = null;
        if (facts.TryGetValue("monthlyContributions", out var rawContribs))
        {
            contributions = rawContribs switch
            {
                IReadOnlyList<MonthlyContribution> roList => roList,
                IEnumerable<MonthlyContribution> en => en.ToList(),
                _ => null,
            };
        }

        if (contributions is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: monthlyContributions.");
        }

        if (!DocxRenderHelpers.TryGet<decimal>(facts, "totalAmountMdl", out var totalAmountMdl))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: totalAmountMdl.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("ADEVERINȚĂ DE COTIZARE"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Beneficiary block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Beneficiar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Contribution period. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Perioada de cotizare: {DocxRenderHelpers.UtcDateFormat(fromUtc)} — "
                + DocxRenderHelpers.UtcDateFormat(toUtc),
                bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Monthly contributions table (header row + one row per month). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Cotizații lunare:", bold: true));
            var rows = new List<KeyValuePair<string, string>>(contributions.Count + 1)
            {
                // Header row.
                new("Luna", "Cuantum cotizare"),
            };
            foreach (var c in contributions)
            {
                rows.Add(new KeyValuePair<string, string>(
                    c.Month ?? string.Empty,
                    DocxRenderHelpers.MoneyFormat(c.AmountMdl)));
            }
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Total amount (bold). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Total: {DocxRenderHelpers.MoneyFormat(totalAmountMdl)}",
                bold: true));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
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
