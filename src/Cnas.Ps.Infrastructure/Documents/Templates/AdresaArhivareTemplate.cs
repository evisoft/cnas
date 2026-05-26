using System.Globalization;
using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Annex 7 template — <em>Adresă privind arhivarea dosarului</em>
/// (notice that a dossier has been archived / closed).
/// </summary>
/// <remarks>
/// <para>
/// Issued when CNAS formally closes a dossier and moves it to the archive store
/// (typically after cessation of payment + statutory cooling-off period). The notice
/// identifies the archive location and, optionally, the retention period in years so
/// the recipient knows when the records may be lawfully destroyed.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold, 14pt).</item>
///   <item>Recipient line.</item>
///   <item>Body paragraph (justified) describing the archival action.</item>
///   <item>Key/value table: dossier id, archival reason, archive location, archived on.</item>
///   <item>Optional retention-years line (when supplied).</item>
///   <item>Signature block.</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>recipientFullName</c> — <see cref="string"/> addressed party.</item>
///   <item><c>dossierSqid</c> — <see cref="string"/> (pre-encoded; CLAUDE.md RULE 3).</item>
///   <item><c>archivalReason</c> — <see cref="string"/> human-readable reason for archival.</item>
///   <item><c>archiveLocation</c> — <see cref="string"/> physical / logical archive coordinate.</item>
///   <item><c>archivedOnUtc</c> — <see cref="DateTime"/> (UTC) instant the dossier was archived.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>retentionYears</c> — <see cref="int"/>. When present, a "Termen de păstrare"
///   line is rendered; when omitted the line is skipped (no exception).</item>
/// </list>
/// </remarks>
public sealed class AdresaArhivareTemplate : IDocxTemplate
{
    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "adresa-arhivare";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<string>(facts, "recipientFullName", out var recipientFullName) || string.IsNullOrWhiteSpace(recipientFullName))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: recipientFullName.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "dossierSqid", out var dossierSqid) || string.IsNullOrWhiteSpace(dossierSqid))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: dossierSqid.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "archivalReason", out var archivalReason) || string.IsNullOrWhiteSpace(archivalReason))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: archivalReason.");
        }

        if (!DocxRenderHelpers.TryGet<string>(facts, "archiveLocation", out var archiveLocation) || string.IsNullOrWhiteSpace(archiveLocation))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: archiveLocation.");
        }

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "archivedOnUtc", out var archivedOnUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: archivedOnUtc.");
        }

        // ── Optional retention-years line. ──
        var hasRetentionYears = DocxRenderHelpers.TryGet<int>(facts, "retentionYears", out var retentionYears);

        // MemoryStream lifetime nesting: keep `ms` open across `ms.ToArray()` by closing
        // the package first (the inner using).
        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("ADRESĂ PRIVIND ARHIVAREA DOSARULUI"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Recipient line. ──
            body.AppendChild(DocxRenderHelpers.Paragraph($"Destinatar: {recipientFullName}", bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Body intro paragraph (justified). ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Vă informăm că dosarul cu identificatorul {dossierSqid} a fost închis "
                + "și transferat în arhiva instituției conform procedurilor interne în vigoare.",
                alignment: JustificationValues.Both));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Key/value table. ──
            var rows = new[]
            {
                new KeyValuePair<string, string>("Identificator dosar", dossierSqid!),
                new KeyValuePair<string, string>("Motivul arhivării", archivalReason!),
                new KeyValuePair<string, string>("Locația în arhivă", archiveLocation!),
                new KeyValuePair<string, string>(
                    "Data arhivării",
                    DocxRenderHelpers.UtcDateFormat(archivedOnUtc)),
            };
            body.AppendChild(DocxRenderHelpers.KeyValueTable(rows));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Optional retention-years line. ──
            if (hasRetentionYears)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"Termen de păstrare: {retentionYears.ToString(CultureInfo.InvariantCulture)} ani.",
                    bold: true));
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            }

            // ── Signature block. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Data: ____________________",
                alignment: JustificationValues.Left));
            body.AppendChild(DocxRenderHelpers.Paragraph(
                "Responsabil arhivă ____________________",
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
