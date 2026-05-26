using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Cerere de transfer al dosarului</em>
/// (citizen-facing inter-branch dossier transfer request).
/// </summary>
/// <remarks>
/// <para>
/// Counterpart to <see cref="AdresaDosarSocialTemplate"/> (the internal inter-branch
/// cover letter): this template renders the citizen-signed request that initiates the
/// transfer. It captures source/destination branches and the reason (typically a
/// permanent change of residence). The signature block leaves space for the applicant
/// rather than the CNAS director.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient line (destination branch addressed).</item>
///   <item>Applicant block (full name, IDNP, dossier id).</item>
///   <item>Key/value table: source branch, destination branch, requested on.</item>
///   <item>Transfer-reason paragraph (justified).</item>
///   <item>Optional contact-phone line.</item>
///   <item>Applicant signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>sourceBranch</c> — <see cref="string"/> branch currently holding the dossier.</item>
///   <item><c>destinationBranch</c> — <see cref="string"/> requested receiving branch.</item>
///   <item><c>transferReason</c> — <see cref="string"/> justification.</item>
///   <item><c>requestedOnUtc</c> — <see cref="DateTime"/> (UTC) request date.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>contactPhone</c> — <see cref="string"/>. When present, a "Telefon de contact"
///   line is rendered above the signature block; when omitted the line is skipped.</item>
/// </list>
/// </remarks>
public sealed class CerereTransferDosarTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "cerere-transfer-dosar";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "transferReason", out var transferReason) || string.IsNullOrWhiteSpace(transferReason))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: transferReason.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "requestedOnUtc", out var requestedOnUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: requestedOnUtc.");
        }

        // ── Optional contact-phone line. ──
        var hasContactPhone = DocxRenderHelpers.TryGet<string>(facts, "contactPhone", out var contactPhone)
            && !string.IsNullOrWhiteSpace(contactPhone);

        // MemoryStream lifetime nesting: keep `ms` open across `ms.ToArray()` by closing
        // the package first (the inner using).
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("CERERE DE TRANSFER AL DOSARULUI"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient line (destination branch). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Către: {destinationBranch}",
                bold: true,
                alignment: JustificationValues.Right));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Applicant block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Solicitant:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Transfer detail table. ──
            var rows = new[]
            {
                new KeyValuePair<string, string>("Sucursala curentă", sourceBranch!),
                new KeyValuePair<string, string>("Sucursala destinatară", destinationBranch!),
                new KeyValuePair<string, string>(
                    "Data cererii",
                    DocxRenderHelpers.UtcDateFormat(requestedOnUtc)),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Reason (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Motivul transferului:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(transferReason!, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Optional contact phone. ──
            if (hasContactPhone)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph($"Telefon de contact: {contactPhone}"));
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            }

            // ── Applicant signature block. ──
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
