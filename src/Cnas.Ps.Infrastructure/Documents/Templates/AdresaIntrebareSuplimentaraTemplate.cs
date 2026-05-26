using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Adresă pentru întrebare suplimentară</em>
/// (formal letter requesting clarifying information from a beneficiary on an open dossier).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="CerereDocumenteLipsaTemplate"/> (formal request for missing
/// documents — file-attachment focus): this template carries a free-form
/// <em>question</em> sent to the beneficiary when CNAS needs additional context (e.g.
/// confirming a residency address, clarifying an employment period) before continuing the
/// examination. The beneficiary is asked to reply in writing by the indicated deadline.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (IDNP, full name, dossier id).</item>
///   <item>Subject line (bold) — short summary of what the inquiry is about.</item>
///   <item>Question body (justified paragraph).</item>
///   <item>Deadline line (bold).</item>
///   <item>Optional reply-channel line (when supplied).</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>subject</c> — <see cref="string"/> short summary line.</item>
///   <item><c>questionText</c> — <see cref="string"/> the actual question.</item>
///   <item><c>replyByUtc</c> — <see cref="DateTime"/> (UTC) deadline for the reply.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>replyChannel</c> — <see cref="string"/>. Preferred reply channel description
///   (e.g. <c>"prin scrisoare oficială la adresa CNAS din mun. Chișinău"</c>). When
///   omitted, the channel line is skipped (no exception).</item>
/// </list>
/// </remarks>
public sealed class AdresaIntrebareSuplimentaraTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "adresa-intrebare-suplimentara";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "subject", out var subject) || string.IsNullOrWhiteSpace(subject))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: subject.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "questionText", out var questionText) || string.IsNullOrWhiteSpace(questionText))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: questionText.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "replyByUtc", out var replyByUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: replyByUtc.");
        }

        // ── Optional reply channel. ──
        var hasReplyChannel = DocxRenderHelpers.TryGet<string>(facts, "replyChannel", out var replyChannel)
            && !string.IsNullOrWhiteSpace(replyChannel);

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("ADRESĂ — ÎNTREBARE SUPLIMENTARĂ"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Subject line. ──
            body.AppendChild(DocxRenderHelpers.Paragraph($"Subiect: {subject}", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Question body. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Întrebarea noastră:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(questionText, alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Deadline (bold). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Vă rugăm să transmiteți răspunsul până la data de {DocxRenderHelpers.UtcDateFormat(replyByUtc)}.",
                bold: true));

            // ── Optional reply channel. ──
            if (hasReplyChannel)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"Canal de răspuns recomandat: {replyChannel}"));
            }

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Funcționar CNAS ____________________",
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
