using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Notificare privind plata transferată</em>
/// (notification that an active payment stream has been transferred to a new
/// account / payment method).
/// </summary>
/// <remarks>
/// <para>
/// Issued when CNAS reroutes an ongoing benefit payment (e.g. from cash-at-counter to
/// bank-transfer, from one bank to another, from the beneficiary's account to a
/// representative's account). The notification confirms what was changed, when the
/// change takes effect, and gives the beneficiary an opportunity to object before the
/// next disbursement run.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient block (IDNP, full name, dossier id).</item>
///   <item>Key/value table: previous method, new method, effective-from date.</item>
///   <item>Optional reason paragraph (when supplied).</item>
///   <item>Objection-rights notice in italic small font.</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>beneficiaryIdnp</c> — <see cref="string"/>.</item>
///   <item><c>beneficiaryFullName</c> — <see cref="string"/>.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>previousMethod</c> — <see cref="string"/> human-readable previous payment method.</item>
///   <item><c>newMethod</c> — <see cref="string"/> human-readable new payment method.</item>
///   <item><c>effectiveFromUtc</c> — <see cref="DateTime"/> (UTC) date the transfer takes effect.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>transferReason</c> — <see cref="string"/>. Free-form explanation; when omitted
///   the reason section is skipped (no exception).</item>
/// </list>
/// </remarks>
public sealed class NotificarePlataTransferataTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "notificare-plata-transferata";

    /// <summary>
    /// Objection-rights notice rendered as italic small text. Held centrally so legal can
    /// review it in one place.
    /// </summary>
    private const string ObjectionRightsNoticeRo =
        "Aveți dreptul să formulați obiecții în termen de 15 zile de la data primirii prezentei "
        + "notificări, în conformitate cu legislația privind asigurările sociale de stat.";

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

        if (!DocxRenderHelpers.TryGet<string>(facts, "previousMethod", out var previousMethod) || string.IsNullOrWhiteSpace(previousMethod))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: previousMethod.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "newMethod", out var newMethod) || string.IsNullOrWhiteSpace(newMethod))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: newMethod.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "effectiveFromUtc", out var effectiveFromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: effectiveFromUtc.");
        }

        // ── Optional transfer reason. ──
        var hasTransferReason = DocxRenderHelpers.TryGet<string>(facts, "transferReason", out var transferReason)
            && !string.IsNullOrWhiteSpace(transferReason);

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("NOTIFICARE — PLATĂ TRANSFERATĂ"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Destinatar:", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Nume complet: {fullName}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"IDNP: {idnp}"));
            body.AppendChild(DocxRenderHelpers.Paragraph($"Dosar: {dossierSqid}"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Transfer details table. ──
            var rows = new[]
            {
                new KeyValuePair<string, string>("Modalitate precedentă", previousMethod!),
                new KeyValuePair<string, string>("Modalitate nouă", newMethod!),
                new KeyValuePair<string, string>(
                    "Dată intrare în vigoare",
                    DocxRenderHelpers.UtcDateFormat(effectiveFromUtc)),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Optional reason paragraph. ──
            if (hasTransferReason)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph("Motivul transferului:", bold: true));
                body.AppendChild(DocxRenderHelpers.Paragraph(transferReason!, alignment: JustificationValues.Both));
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            }

            // ── Objection-rights notice. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                ObjectionRightsNoticeRo,
                italic: true,
                fontSizeHalfPoints: "16",
                alignment: JustificationValues.Both));
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
