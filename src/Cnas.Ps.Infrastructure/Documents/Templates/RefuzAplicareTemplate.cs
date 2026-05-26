using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Refuz al cererii</em> (refusal of an application).
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title.</item>
///   <item>Recipient block (IDNP, full name, dossier id).</item>
///   <item>Refuse reason as a justified paragraph.</item>
///   <item>Appeal-rights notice in italic, small font, at the bottom.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded — never decode here; CLAUDE.md RULE 3).</item>
///   <item><c>refuseReason</c> — <see cref="string"/>.</item>
///   <item><c>decisionUtc</c> — <see cref="DateTime"/> (UTC).</item>
/// </list>
/// </remarks>
public sealed class RefuzAplicareTemplate : IDocxTemplate
{
    /// <summary>
    /// Romanian appeal-rights notice rendered as italic small text at the bottom of every
    /// refusal. Kept as a class constant so legal can review it in one place — it is
    /// part of the document contract and should not change without sign-off.
    /// </summary>
    private const string AppealRightsNoticeRo =
        "Prezenta decizie poate fi contestată în termen de 30 zile de la data comunicării, "
        + "în conformitate cu legislația privind contenciosul administrativ al Republicii Moldova.";

    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "refuz-aplicare";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "refuseReason", out var refuseReason) || string.IsNullOrWhiteSpace(refuseReason))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: refuseReason.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "decisionUtc", out var decisionUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: decisionUtc.");
        }

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("REFUZ AL CERERII"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Decision date + refuse reason (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"La data de {DocxRenderHelpers.UtcDateFormat(decisionUtc)} s-a hotărât respingerea cererii.",
                bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph("Motivul refuzului:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(refuseReason, alignment: JustificationValues.Both));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Appeal-rights notice (italic, small font, at the bottom). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                AppealRightsNoticeRo,
                italic: true,
                fontSizeHalfPoints: "16",
                alignment: JustificationValues.Both));

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
