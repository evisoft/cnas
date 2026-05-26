using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Decizia de stabilire a pensiei</em> (pension award decision).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title, centered, bold, 14pt.</item>
///   <item>Beneficiary block (IDNP, full name, address).</item>
///   <item>Service code and Romanian title.</item>
///   <item>Granted-from date (UTC).</item>
///   <item>Monthly amount in MDL, formatted <c>"#,##0.00 MDL"</c>.</item>
///   <item>Signature block (date placeholder + signatory line).</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryAddress</c> — <see cref="string"/>.</item>
///   <item><c>serviceCode</c> — <see cref="string"/>.</item>
///   <item><c>serviceTitleRo</c> — <see cref="string"/>.</item>
///   <item><c>grantedFromUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>monthlyAmountMdl</c> — <see cref="decimal"/>.</item>
/// </list>
/// </remarks>
public sealed class DeciziaPensieTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "decizia-pensie";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // ── Required-fact validation. We surface the offending key in the message so the
        //    caller can correct the call without a debugger. ──
        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryIdnp", out var idnp) || string.IsNullOrWhiteSpace(idnp))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryIdnp.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryFullName", out var fullName) || string.IsNullOrWhiteSpace(fullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryFullName.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryAddress", out var address) || string.IsNullOrWhiteSpace(address))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryAddress.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "serviceCode", out var serviceCode) || string.IsNullOrWhiteSpace(serviceCode))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: serviceCode.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "serviceTitleRo", out var serviceTitle) || string.IsNullOrWhiteSpace(serviceTitle))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: serviceTitleRo.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "grantedFromUtc", out var grantedFromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: grantedFromUtc.");
        }

        if (!DocxRenderHelpers.TryGet<decimal>(facts, "monthlyAmountMdl", out var monthlyAmountMdl))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: monthlyAmountMdl.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            // ── Title (centered, bold, 14pt). ──
            body.AppendChild(DocxRenderHelpers.Heading("DECIZIE DE STABILIRE A PENSIEI"));

            // Spacer.
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Beneficiary block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Beneficiar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Adresa: {address}"));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Service block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph($"Cod serviciu: {serviceCode}", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Denumire serviciu: {serviceTitle}"));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Granted from + monthly amount. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Se acordă începând cu: {DocxRenderHelpers.UtcDateFormat(grantedFromUtc)}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Cuantum lunar: {DocxRenderHelpers.MoneyFormat(monthlyAmountMdl)}",
                bold: true));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block: date placeholder (left), signatory line (right). ──
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
