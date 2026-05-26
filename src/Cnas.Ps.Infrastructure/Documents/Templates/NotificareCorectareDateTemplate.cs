using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Notificare privind corectarea datelor cu caracter personal</em>
/// (notification that the citizen's personal data was corrected administratively).
/// </summary>
/// <remarks>
/// <para>
/// Issued whenever CNAS amends a personal-data field of record (name, IDNP, address,
/// civil status, bank account) on its own initiative or in response to an external
/// authoritative source (RSP, sentinel court order). The notification documents the
/// previous and new values side-by-side, the source authorising the correction, and the
/// UTC instant the change took effect.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (IDNP, full name).</item>
///   <item>Key/value table: corrected field, previous value, new value, correction source.</item>
///   <item>Correction-applied UTC timestamp line.</item>
///   <item>Optional case-officer signature line (when supplied).</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>correctedField</c> — <see cref="string"/> human-readable field label.</item>
///   <item><c>previousValue</c> — <see cref="string"/> previously-recorded value.</item>
///   <item><c>newValue</c> — <see cref="string"/> newly-recorded value.</item>
///   <item><c>correctionSource</c> — <see cref="string"/> citation of the authorising source.</item>
///   <item><c>correctionAppliedUtc</c> — <see cref="DateTime"/> (UTC) when the change took effect.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>caseOfficerFullName</c> — <see cref="string"/>. When present, a "Funcționar
///   responsabil" line is rendered above the signature block.</item>
/// </list>
/// </remarks>
public sealed class NotificareCorectareDateTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "notificare-corectare-date";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "correctedField", out var correctedField) || string.IsNullOrWhiteSpace(correctedField))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: correctedField.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "previousValue", out var previousValue) || string.IsNullOrWhiteSpace(previousValue))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: previousValue.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "newValue", out var newValue) || string.IsNullOrWhiteSpace(newValue))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: newValue.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "correctionSource", out var correctionSource) || string.IsNullOrWhiteSpace(correctionSource))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: correctionSource.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "correctionAppliedUtc", out var correctionAppliedUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: correctionAppliedUtc.");
        }

        // ── Optional case-officer signature line. ──
        var hasCaseOfficer = DocxRenderHelpers.TryGet<string>(facts, "caseOfficerFullName", out var caseOfficerFullName)
            && !string.IsNullOrWhiteSpace(caseOfficerFullName);

        // MemoryStream lifetime nesting: keep `ms` open across `ms.ToArray()` by closing
        // the package first (the inner using).
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("NOTIFICARE PRIVIND CORECTAREA DATELOR"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Correction detail table. ──
            var rows = new[]
            {
                new KeyValuePair<string, string>("Câmpul corectat", correctedField!),
                new KeyValuePair<string, string>("Valoarea anterioară", previousValue!),
                new KeyValuePair<string, string>("Valoarea nouă", newValue!),
                new KeyValuePair<string, string>("Sursa corectării", correctionSource!),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Correction-applied timestamp (bold). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Corectarea a fost aplicată la {DocxRenderHelpers.UtcDateFormat(correctionAppliedUtc)}.",
                bold: true));

            // ── Optional case-officer line. ──
            if (hasCaseOfficer)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"Funcționar responsabil: {caseOfficerFullName}"));
            }

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
