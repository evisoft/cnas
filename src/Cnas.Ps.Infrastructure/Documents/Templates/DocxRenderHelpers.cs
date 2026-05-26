using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Cnas.Ps.Infrastructure.Documents.Templates;

/// <summary>
/// Shared OpenXML building blocks used by all Annex 7 templates. Centralises the
/// recurrent paragraph / run / table / formatting primitives so individual templates can
/// focus on layout intent rather than the verbose OpenXML object graph.
/// </summary>
/// <remarks>
/// All helpers are pure (return-only, no I/O) and thread-safe — they only allocate
/// fresh OpenXML nodes. Internal visibility intentionally restricts the surface to the
/// Infrastructure assembly; the test project sees these via <c>InternalsVisibleTo</c>
/// declared on <c>Cnas.Ps.Infrastructure.csproj</c>.
/// </remarks>
internal static class DocxRenderHelpers
{
    /// <summary>
    /// Builds a body paragraph carrying <paramref name="text"/>. When
    /// <paramref name="bold"/> or <paramref name="italic"/> is set, the run carries the
    /// corresponding <c>RunProperties</c>. The text is wrapped with
    /// <c>SpaceProcessingModeValues.Preserve</c> so leading/trailing whitespace survives
    /// the OpenXML serialiser.
    /// </summary>
    /// <param name="text">Paragraph text content.</param>
    /// <param name="bold">Whether to mark the run as bold.</param>
    /// <param name="italic">Whether to mark the run as italic.</param>
    /// <param name="fontSizeHalfPoints">
    /// Optional font size in half-points (OpenXML convention) — e.g. <c>"20"</c> for 10pt.
    /// Pass <see langword="null"/> to inherit the document default.
    /// </param>
    /// <param name="alignment">Optional paragraph alignment.</param>
    /// <returns>A new <see cref="Paragraph"/> ready to be appended to a <c>Body</c>.</returns>
    public static Paragraph Paragraph(
        string text,
        bool bold = false,
        bool italic = false,
        string? fontSizeHalfPoints = null,
        JustificationValues? alignment = null)
    {
        var runProps = new RunProperties();
        if (bold)
        {
            runProps.Append(new Bold());
        }

        if (italic)
        {
            runProps.Append(new Italic());
        }

        if (!string.IsNullOrEmpty(fontSizeHalfPoints))
        {
            runProps.Append(new FontSize { Val = fontSizeHalfPoints });
        }

        var run = new Run(runProps, new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });
        var paragraph = new Paragraph(run);

        if (alignment.HasValue)
        {
            paragraph.ParagraphProperties = new ParagraphProperties(
                new Justification { Val = alignment.Value });
        }

        return paragraph;
    }

    /// <summary>
    /// Builds a heading paragraph — bold, 14pt (28 half-points), centered by default.
    /// Used by every Annex 7 template for the top-of-document title.
    /// </summary>
    /// <param name="text">Heading text.</param>
    /// <param name="alignment">Heading alignment (default: <see cref="JustificationValues.Center"/>).</param>
    /// <returns>A new <see cref="Paragraph"/> styled as a heading.</returns>
    public static Paragraph Heading(string text, JustificationValues? alignment = null)
        => Paragraph(
            text,
            bold: true,
            fontSizeHalfPoints: "28",
            alignment: alignment ?? JustificationValues.Center);

    /// <summary>
    /// Builds a bullet-list paragraph. Uses Word's built-in <c>ListParagraph</c> style and
    /// a numbering reference of 1 — the bullet character is supplied by the runs
    /// themselves so the document is readable without requiring a numbering definitions
    /// part (kept minimal for the inlined templates).
    /// </summary>
    /// <param name="text">Bullet text.</param>
    /// <returns>A new bullet <see cref="Paragraph"/>.</returns>
    public static Paragraph Bullet(string text)
    {
        // Compose the run manually so we can prefix the bullet glyph without disturbing
        // the SpaceProcessingModeValues.Preserve attribute that the underlying Paragraph
        // helper sets — concatenating up front is the simplest path that still renders
        // correctly in Word, LibreOffice, and Google Docs.
        var paragraph = Paragraph($"•  {text}");
        paragraph.ParagraphProperties = new ParagraphProperties(
            new ParagraphStyleId { Val = "ListParagraph" },
            new Indentation { Left = "360" });
        return paragraph;
    }

    /// <summary>
    /// Builds a two-column key/value <see cref="Table"/>. The left column ("Câmp") is
    /// rendered with bold text; the right column ("Valoare") with normal text. Cell
    /// borders are single-line 4-pt gray, matching the Annex 7 Word-template visual
    /// reference.
    /// </summary>
    /// <param name="rows">Sequence of (label, value) pairs.</param>
    /// <returns>A new <see cref="Table"/> ready to be appended to a <c>Body</c>.</returns>
    public static Table KeyValueTable(IEnumerable<KeyValuePair<string, string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var table = new Table();

        // ── Table properties: thin gray borders on every edge ──
        var borders = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new RightBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "808080" });

        var tableProps = new TableProperties(
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            borders);
        table.AppendChild(tableProps);

        foreach (var kvp in rows)
        {
            var tr = new TableRow();
            tr.AppendChild(BuildCell(kvp.Key ?? string.Empty, bold: true));
            tr.AppendChild(BuildCell(kvp.Value ?? string.Empty, bold: false));
            table.AppendChild(tr);
        }

        return table;
    }

    /// <summary>
    /// Formats a Moldovan-leu monetary amount as <c>"#,##0.00 MDL"</c> using invariant
    /// culture so the rendered document is stable regardless of host locale.
    /// </summary>
    /// <param name="amount">Amount in MDL (no currency conversion is performed).</param>
    /// <returns>Formatted string like <c>"1,234.56 MDL"</c>.</returns>
    public static string MoneyFormat(decimal amount)
        => string.Create(CultureInfo.InvariantCulture, $"{amount:#,##0.00} MDL");

    /// <summary>
    /// Formats a UTC <see cref="DateTime"/> as <c>"yyyy-MM-dd HH:mm 'UTC'"</c> using
    /// invariant culture. Callers should ensure <see cref="DateTime.Kind"/> is
    /// <see cref="DateTimeKind.Utc"/> (CLAUDE.md UTC Everywhere).
    /// </summary>
    /// <param name="utc">UTC timestamp.</param>
    /// <returns>Formatted invariant-culture timestamp.</returns>
    public static string UtcFormat(DateTime utc)
        => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a UTC <see cref="DateTime"/> as <c>"yyyy-MM-dd"</c> (date-only) using
    /// invariant culture. Used when the time-of-day component is not meaningful (e.g.
    /// "granted from" or "deadline" fields).
    /// </summary>
    /// <param name="utc">UTC timestamp.</param>
    /// <returns>Formatted invariant-culture date.</returns>
    public static string UtcDateFormat(DateTime utc)
        => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a required fact of type <typeparamref name="T"/> from the supplied facts
    /// dictionary. Used by templates at the top of <c>Render</c> to enforce the
    /// per-template required-key contract.
    /// </summary>
    /// <typeparam name="T">Expected runtime type of the fact value.</typeparam>
    /// <param name="facts">Facts dictionary.</param>
    /// <param name="key">Required key.</param>
    /// <param name="value">On success, the typed value.</param>
    /// <returns><see langword="true"/> if the key is present and of type <typeparamref name="T"/>.</returns>
    public static bool TryGet<T>(IReadOnlyDictionary<string, object?> facts, string key, out T? value)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Builds a single-cell paragraph (label or value) inside a table row.
    /// </summary>
    /// <param name="text">Cell text content.</param>
    /// <param name="bold">Whether the run should be bold.</param>
    /// <returns>A new <see cref="TableCell"/>.</returns>
    private static TableCell BuildCell(string text, bool bold)
    {
        var cell = new TableCell(Paragraph(text, bold: bold));
        cell.PrependChild(new TableCellProperties(
            new TableCellWidth { Width = "2500", Type = TableWidthUnitValues.Pct }));
        return cell;
    }
}
