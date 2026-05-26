using Cnas.Ps.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// R2002 / Annex 7 §8.7.3 — <em>Raport audit activitate</em>
/// (audit-activity report covering user actions over a reporting window).
/// </summary>
/// <remarks>
/// <para>
/// Standard <em>Raport</em> template per criteria 8.7.3.3 (audit / security
/// aggregates). Distinct from <see cref="RaportControlInternTemplate"/> —
/// this template is the printable wrapper around the
/// <c>RPT-AUDIT-EVENTS-BY-ACTOR</c> / <c>RPT-AUDIT-EVENTS-BY-ACTION</c>
/// family. It groups per-actor activity for legal / disciplinary review.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item>Title (centered, bold).</item>
///   <item>Audit-period line (<c>fromUtc</c> – <c>toUtc</c>, UTC dates).</item>
///   <item>Per-actor activity table: Actor → EventCount.</item>
///   <item>Critical-event highlight row (if any).</item>
///   <item>UTC generation footer.</item>
/// </list>
/// <para>Required fact keys (camelCase):</para>
/// <list type="bullet">
///   <item><c>fromUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>toUtc</c> — <see cref="DateTime"/> (UTC).</item>
///   <item><c>actorRows</c> — <see cref="IReadOnlyList{T}"/> of <see cref="ActorRow"/>.</item>
/// </list>
/// <para>Optional fact keys:</para>
/// <list type="bullet">
///   <item><c>criticalEventCount</c> — <see cref="int"/>. When &gt; 0 renders a
///   highlight paragraph; when omitted the highlight is skipped.</item>
/// </list>
/// </remarks>
public sealed class RaportAuditActivitateTemplate : IDocxTemplate
{
    /// <summary>
    /// One row in the per-actor activity table.
    /// </summary>
    /// <param name="ActorId">
    /// Stable actor identifier (Sqid-encoded user id or service marker like
    /// <c>system:r0189-evaluator</c>). The caller is responsible for choosing
    /// the display label — the template does NOT decode internal IDs.
    /// </param>
    /// <param name="EventCount">Number of audit events attributed to the actor.</param>
    public sealed record ActorRow(string ActorId, long EventCount);

    /// <summary>Stable template code (wire-format identifier).</summary>
    public const string Code = "raport-audit-activitate";

    /// <inheritdoc />
    public string TemplateCode => Code;

    /// <inheritdoc />
    public Result<byte[]> Render(IReadOnlyDictionary<string, object?> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "fromUtc", out var fromUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: fromUtc.");
        }
        if (!DocxRenderHelpers.TryGet<DateTime>(facts, "toUtc", out var toUtc))
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: toUtc.");
        }

        IReadOnlyList<ActorRow>? rows = null;
        if (facts.TryGetValue("actorRows", out var raw))
        {
            rows = raw switch
            {
                IReadOnlyList<ActorRow> roList => roList,
                IEnumerable<ActorRow> en => en.ToList(),
                _ => null,
            };
        }
        if (rows is null)
        {
            return Result<byte[]>.Failure(ErrorCodes.TemplateMissingFacts, "Missing required fact: actorRows.");
        }

        // ── Optional critical-event highlight. ──
        var hasCritical = DocxRenderHelpers.TryGet<int>(facts, "criticalEventCount", out var criticalCount)
            && criticalCount > 0;

        using var ms = new MemoryStream();
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            body.AppendChild(DocxRenderHelpers.Heading("RAPORT AUDIT ACTIVITATE"));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Period. ──
            body.AppendChild(DocxRenderHelpers.Paragraph(
                $"Perioada raportului: {DocxRenderHelpers.UtcDateFormat(fromUtc)} — "
                + DocxRenderHelpers.UtcDateFormat(toUtc),
                bold: true));
            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Per-actor table. ──
            body.AppendChild(DocxRenderHelpers.Paragraph("Activitate pe actor:", bold: true));
            var tableRows = new List<KeyValuePair<string, string>>(rows.Count + 1)
            {
                // Header row.
                new("Actor", "Număr evenimente"),
            };
            foreach (var r in rows)
            {
                tableRows.Add(new KeyValuePair<string, string>(
                    r.ActorId ?? string.Empty,
                    r.EventCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            body.AppendChild(DocxRenderHelpers.KeyValueTable(tableRows));

            body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));

            // ── Optional critical-event highlight. ──
            if (hasCritical)
            {
                body.AppendChild(DocxRenderHelpers.Paragraph(
                    $"ATENȚIE: au fost înregistrate {criticalCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                    + "evenimente cu severitate critică în această perioadă.",
                    bold: true,
                    alignment: JustificationValues.Both));
                body.AppendChild(DocxRenderHelpers.Paragraph(string.Empty));
            }

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
