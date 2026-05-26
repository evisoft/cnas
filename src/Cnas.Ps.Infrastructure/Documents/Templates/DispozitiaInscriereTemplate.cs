using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Dispoziție privind înscrierea</em> (enrolment disposition).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title.</item>
///   <item>Numbered/bulleted clauses (one bullet per clause).</item>
///   <item>Recipient block (IDNP, full name).</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>effectiveFromUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>clauses</c> — <see cref="IReadOnlyList{T}"/> or <see cref="List{T}"/> of <see cref="string"/>.</item>
/// </list>
/// </remarks>
public sealed class DispozitiaInscriereTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "dispozitia-inscriere";

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

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "effectiveFromUtc", out var effectiveFromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: effectiveFromUtc.");
        }

        // Accept both List<string> and IReadOnlyList<string> at the boundary.
        IReadOnlyList<string>? clauses = null;
        if (facts.TryGetValue("clauses", out var rawClauses))
        {
            clauses = rawClauses switch
            {
                IReadOnlyList<string> roList => roList,
                IEnumerable<string> en => en.ToList(),
                _ => null,
            };
        }

        if (clauses is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: clauses.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("DISPOZIȚIE PRIVIND ÎNSCRIEREA"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Cu valabilitate de la: {DocxRenderHelpers.UtcDateFormat(effectiveFromUtc)}",
                bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Clauses. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Se dispune:", bold: true));
            foreach (var clause in clauses)
            {
                body.AppendChild(DocxRenderHelpers.Bullet(clause ?? string.Empty));
            }

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Către:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Director CNAS ____________________",
                alignment: JustificationValues.Right));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
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
