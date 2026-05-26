using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Adresă privind dosarul social</em>
/// (administrative cover letter for a social-dossier transfer between CTAS branches).
/// </summary>
/// <remarks>
/// <para>
/// Used when a beneficiary's social dossier is forwarded from one CTAS territorial
/// branch (Casă Teritorială de Asigurări Sociale) to another — typically following a
/// change of residency. The letter accompanies the physical/electronic dossier transfer
/// so the receiving branch can register and continue processing without ambiguity.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title.</item>
///   <item>Source-branch ("De la") and destination-branch ("Către") block.</item>
///   <item>Transfer date line.</item>
///   <item>Subject line: "Transmiterea dosarului social nr. {dossierSqid}".</item>
///   <item>Beneficiary block (IDNP + full name).</item>
///   <item>Optional transfer-reason paragraph (when supplied).</item>
///   <item>Receipt-acknowledgement footer.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>sourceBranch</c> — <see cref="string"/>.</item>
///   <item><c>destinationBranch</c> — <see cref="string"/>.</item>
///   <item><c>transferUtc</c> — <see cref="DateTime"/> (UTC).</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>transferReason</c> — <see cref="string"/>. When present, an explanatory paragraph
///   is appended; when absent, the section is skipped without error.</item>
/// </list>
/// </remarks>
public sealed class AdresaDosarSocialTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "adresa-dosar-social";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "dossierSqid", out var dossierSqid) || string.IsNullOrWhiteSpace(dossierSqid))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: dossierSqid.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "sourceBranch", out var sourceBranch) || string.IsNullOrWhiteSpace(sourceBranch))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: sourceBranch.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "destinationBranch", out var destinationBranch) || string.IsNullOrWhiteSpace(destinationBranch))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: destinationBranch.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "transferUtc", out var transferUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: transferUtc.");
        }

        // ── Optional transfer-reason paragraph. ──
        var hasReason = DocxRenderHelpers.TryGet<string>(facts, "transferReason", out var transferReason)
            && !string.IsNullOrWhiteSpace(transferReason);

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("ADRESĂ PRIVIND DOSARUL SOCIAL"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Source / destination block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph($"De la: {sourceBranch}", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Către: {destinationBranch}", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Data transmiterii: {DocxRenderHelpers.UtcDateFormat(transferUtc)}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Subject. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Subiect: Transmiterea dosarului social nr. {dossierSqid}",
                italic: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Beneficiary block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Beneficiar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Optional transfer reason. ──
            if (hasReason)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph("Motivul transmiterii:", bold: true));
                body.AppendChild(DocxRenderHelpers.Paragraph(transferReason!, alignment: JustificationValues.Both));
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            }

            // ── Receipt-acknowledgement footer. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Vă rugăm să confirmați recepționarea dosarului în termen de 5 zile lucrătoare.",
                italic: true,
                alignment: JustificationValues.Both));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Șef {sourceBranch} ____________________",
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
