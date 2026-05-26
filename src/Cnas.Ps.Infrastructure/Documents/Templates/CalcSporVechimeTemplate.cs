using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Calculul sporului de vechime</em> (seniority-bonus calculation sheet).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Beneficiary block (IDNP, full name).</item>
///   <item>2-column table of service periods — left "Perioada" (from – to), right "Vechime (ani)".</item>
///   <item>Total years of service in bold (right-aligned).</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>periods</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/> of <see cref="ServicePeriod"/>.</item>
///   <item><c>totalYears</c> — <see cref="decimal"/>.</item>
/// </list>
/// </remarks>
public sealed class CalcSporVechimeTemplate : IDocxTemplate
{
    /// <summary>
    /// One row in the periods table. <see cref="From"/> and <see cref="To"/> are pre-formatted
    /// date strings — the template renders them verbatim so the caller controls the locale
    /// of the period boundaries. <see cref="Years"/> is rendered with two decimals using
    /// invariant culture.
    /// </summary>
    /// <param name="From">Period start (pre-formatted, e.g. <c>"2000-01-01"</c>).</param>
    /// <param name="To">Period end (pre-formatted, e.g. <c>"2010-12-31"</c>).</param>
    /// <param name="Years">Years of service contributed by this period (≥ 0).</param>
    public sealed record ServicePeriod(string From, string To, decimal Years);

    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "calc-spor-vechime";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // ── Required-fact validation. ──
        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryIdnp", out var idnp) || string.IsNullOrWhiteSpace(idnp))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryIdnp.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryFullName", out var fullName) || string.IsNullOrWhiteSpace(fullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryFullName.");
        }

        // Accept both List<ServicePeriod> and IReadOnlyList<ServicePeriod> at the boundary.
        IReadOnlyList<ServicePeriod>? periods = null;
        if (facts.TryGetValue("periods", out var rawPeriods))
        {
            periods = rawPeriods switch
            {
                IReadOnlyList<ServicePeriod> roList => roList,
                IEnumerable<ServicePeriod> en => en.ToList(),
                _ => null,
            };
        }

        if (periods is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: periods.");
        }

        if (!DocxRenderHelpers.TryGet<decimal>(facts, "totalYears", out var totalYears))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: totalYears.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("CALCULUL SPORULUI DE VECHIME"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Beneficiary block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Beneficiar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Periods table (header row + one row per period). ──
            // KeyValueTable renders the left column bold, which doubles as the header
            // styling for the first row (period label). For body rows the left column
            // carries the period range — still bold to align with the existing visual
            // convention used by FisaDeCalculTemplate.
            body.AppendChild(DocxRenderHelpers.Paragraph("Perioade de activitate:", bold: true));
            var rows = new List<KeyValuePair<string, string>>(periods.Count + 1)
            {
                // Header row.
                new("Perioada", "Vechime (ani)"),
            };
            foreach (var p in periods)
            {
                rows.Add(new KeyValuePair<string, string>(
                    $"{p.From} — {p.To}",
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.Years:0.00}")));
            }
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Total years (bold, right-aligned). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Total vechime: {totalYears:0.00} ani"),
                bold: true,
                alignment: JustificationValues.Right));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── UTC footer (rendered only when caller supplies the clock value). ──
            // Templates are deliberately clock-free: the caller (DocumentGenerationService,
            // wired with ICnasTimeProvider) is responsible for adding `generatedAtUtc` to
            // the facts dictionary. Omitting the fact omits the footer rather than leaking
            // a raw DateTime.UtcNow call inside the renderer (CLAUDE.md "UTC Everywhere").
            if (DocxRenderHelpers.TryGet<DateTime>(facts, "generatedAtUtc", out var generatedAtUtc))
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"Generat automat la {DocxRenderHelpers.UtcFormat(generatedAtUtc)}",
                    italic: true,
                    fontSizeHalfPoints: "16"));
            }

            mainPart.Document = new WordDocument(body);
            mainPart.Document.Save();
        }

        return Result<byte[]>.Success(ms.ToArray());
    }
}
