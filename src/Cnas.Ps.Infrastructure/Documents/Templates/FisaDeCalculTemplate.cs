using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Fișa de calcul</em> (calculation sheet).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title.</item>
///   <item>Beneficiary block (IDNP, full name).</item>
///   <item>Service-code line.</item>
///   <item>2-column key/value table listing every supplied calculation fact (Romanian label → formatted value).</item>
///   <item>Total amount paragraph in bold (if <c>totalAmountMdl</c> is supplied).</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>serviceCode</c> — <see cref="string"/>.</item>
///   <item><c>calculationFacts</c> — <see cref="IReadOnlyDictionary{TKey, TValue}"/> or <see cref="Dictionary{TKey, TValue}"/> of <see cref="string"/>→<see cref="string"/>.</item>
/// </list>
/// <para>Optional facts:</para>
/// <list type="bullet">
///   <item><c>totalAmountMdl</c> — <see cref="decimal"/>; rendered in bold as <c>"#,##0.00 MDL"</c>.</item>
/// </list>
/// </remarks>
public sealed class FisaDeCalculTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "fisa-de-calcul";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "serviceCode", out var serviceCode) || string.IsNullOrWhiteSpace(serviceCode))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: serviceCode.");
        }

        // ── calculationFacts may arrive as either an IDictionary or IReadOnlyDictionary
        //    depending on the caller; accept both to be permissive at the boundary. ──
        IReadOnlyDictionary<string, string>? calculationFacts = null;
        if (facts.TryGetValue("calculationFacts", out var rawCalc))
        {
            calculationFacts = rawCalc switch
            {
                IReadOnlyDictionary<string, string> roDict => roDict,
                IDictionary<string, string> dict => new Dictionary<string, string>(dict),
                _ => null,
            };
        }

        if (calculationFacts is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: calculationFacts.");
        }

        // Optional — bold total at the end.
        decimal? totalAmountMdl = null;
        if (DocxRenderHelpers.TryGet<decimal>(facts, "totalAmountMdl", out var amt))
        {
            totalAmountMdl = amt;
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("FIȘĂ DE CALCUL"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            body.AppendChild(DocxRenderHelpers.Paragraph("Beneficiar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            body.AppendChild(DocxRenderHelpers.Paragraph($"Cod serviciu: {serviceCode}", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Calculation table. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Detalii calcul:", bold: true));
            body.AppendChild(DocxRenderHelpers.KeyValueTable(calculationFacts));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            if (totalAmountMdl.HasValue)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"Total: {DocxRenderHelpers.MoneyFormat(totalAmountMdl.Value)}",
                    bold: true,
                    alignment: JustificationValues.Right));
            }

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            // ── UTC footer (rendered only when caller supplies the clock value). ──
            // Templates are deliberately clock-free: DocumentGenerationService (wired with
            // ICnasTimeProvider) is responsible for adding `generatedAtUtc` to the facts
            // dictionary. Omitting the fact omits the footer rather than leaking a raw
            // DateTime.UtcNow call inside the renderer (CLAUDE.md "UTC Everywhere").
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
