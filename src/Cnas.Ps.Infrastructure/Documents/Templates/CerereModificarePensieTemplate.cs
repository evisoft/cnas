using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// R2000 / Annex 7 §8.7.1 — <em>Cerere modificare pensie</em>
/// (formal request to modify an existing pension award — change of
/// payment account, change of postal address, change of beneficiary
/// representation, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the existing <see cref="CerereRecalculPlataTemplate"/> —
/// the recalculation cerere triggers re-computation of the amount; this
/// template captures non-financial modifications that nonetheless require
/// CNAS to update the dossier (new bank account, address, contact, or
/// representative).
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold).</item>
///   <item>Beneficiary block (full name, IDNP, dossier sqid).</item>
///   <item>Modification-type label (e.g. "Schimbare cont bancar").</item>
///   <item>Modification details (justified paragraph).</item>
///   <item>Effective-from date.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>modificationType</c> — <see cref="string"/>.</item>
///   <item><c>modificationDetails</c> — <see cref="string"/>.</item>
///   <item><c>effectiveFromUtc</c> — <see cref="DateTime"/> (UTC).</item>
/// </list>
/// </remarks>
public sealed class CerereModificarePensieTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "cerere-modificare-pensie";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryFullName", out var fullName) || string.IsNullOrWhiteSpace(fullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryFullName.");
        }
        if (!DocxRenderHelpers.TryGet<string>(facts, "beneficiaryIdnp", out var idnp) || string.IsNullOrWhiteSpace(idnp))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: beneficiaryIdnp.");
        }
        if (!DocxRenderHelpers.TryGet<string>(facts, "dossierSqid", out var dossierSqid) || string.IsNullOrWhiteSpace(dossierSqid))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: dossierSqid.");
        }
        if (!DocxRenderHelpers.TryGet<string>(facts, "modificationType", out var modificationType) || string.IsNullOrWhiteSpace(modificationType))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: modificationType.");
        }
        if (!DocxRenderHelpers.TryGet<string>(facts, "modificationDetails", out var modificationDetails) || string.IsNullOrWhiteSpace(modificationDetails))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: modificationDetails.");
        }
        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "effectiveFromUtc", out var effectiveFromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: effectiveFromUtc.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("CERERE MODIFICARE PENSIE"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Beneficiary block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Beneficiar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Modification block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Tip modificare:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(modificationType));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            body.AppendChild(DocxRenderHelpers.Paragraph("Detalii:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                modificationDetails,
                alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Effective-from. ──
            var effectiveRows = new[]
            {
                new KeyValuePair<string, string>(
                    "Aplicabil începând cu",
                    DocxRenderHelpers.UtcDateFormat(effectiveFromUtc)),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(effectiveRows));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Semnătura solicitantului ____________________",
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
