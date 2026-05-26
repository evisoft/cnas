using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// R0134 / TOR CF 17.17 — Concrete <see cref="ITemplateCatalogPort"/>. Serialises the
/// template + variant catalog to / from UTF-8 XML or CSV. Imports are upsert-only
/// for VARIANT rows of EXISTING templates; unknown template codes are skipped with
/// a warning rather than auto-created.
/// </summary>
/// <remarks>
/// <para>
/// <b>XML schema.</b> A simple two-level hierarchy:
/// <code>
/// &lt;TemplateCatalog&gt;
///   &lt;Template code="decizia-pensie" defaultLanguage="ro"&gt;
///     &lt;Variant language="ro" subject="..." approved="true" translatorNote="..."&gt;
///       &lt;![CDATA[body text]]&gt;
///     &lt;/Variant&gt;
///     ...
///   &lt;/Template&gt;
/// &lt;/TemplateCatalog&gt;
/// </code>
/// The DOCX blob is intentionally NOT serialised — only metadata + the plain-text
/// body. Round-tripping a translated docx requires the docx upload path on the
/// admin REST surface.
/// </para>
/// <para>
/// <b>CSV format.</b> RFC 4180 with the header row
/// <c>TemplateCode,Language,Subject,Body,Approved,TranslatorNote</c>. Fields that
/// contain commas, double-quotes, or line breaks are double-quoted; embedded
/// double-quotes are doubled. No magic encoding — UTF-8 throughout, no BOM.
/// </para>
/// </remarks>
public sealed class TemplateCatalogPort : ITemplateCatalogPort
{
    private const string CsvHeader = "TemplateCode,Language,Subject,Body,Approved,TranslatorNote";

    private readonly ICnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IAuditService? _audit;

    /// <summary>
    /// Constructs the port. Audit is optional so test harnesses skipping audit wiring
    /// don't have to plumb a no-op double.
    /// </summary>
    /// <param name="db">EF Core context (scoped).</param>
    /// <param name="clock">UTC clock.</param>
    /// <param name="caller">Caller context for audit attribution.</param>
    /// <param name="audit">Optional audit sink.</param>
    /// <exception cref="ArgumentNullException">When a required collaborator is null.</exception>
    public TemplateCatalogPort(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IAuditService? audit = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        _db = db;
        _clock = clock;
        _caller = caller;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> ExportXmlAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);

        var root = new XElement("TemplateCatalog");
        foreach (var (template, variants) in snapshot)
        {
            var templateEl = new XElement("Template",
                new XAttribute("code", template.Code),
                new XAttribute("defaultLanguage", template.DefaultLanguage));
            foreach (var v in variants.OrderBy(x => x.Language, StringComparer.Ordinal))
            {
                var variantEl = new XElement("Variant",
                    new XAttribute("language", v.Language),
                    new XAttribute("subject", v.SubjectOrTitle),
                    new XAttribute("approved", v.IsApproved ? "true" : "false"));
                if (!string.IsNullOrEmpty(v.TranslatorNote))
                {
                    variantEl.Add(new XAttribute("translatorNote", v.TranslatorNote));
                }
                // CDATA preserves line breaks and reserved chars in the body without
                // tortured entity escaping. Body length is capped at 100,000 chars
                // by the upsert validator, so the in-memory XML stays manageable.
                variantEl.Add(new XCData(v.Body));
                templateEl.Add(variantEl);
            }
            root.Add(templateEl);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using (var xw = XmlWriter.Create(ms, settings))
        {
            doc.WriteTo(xw);
        }
        return Result<byte[]>.Success(ms.ToArray());
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> ExportCsvAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadCatalogSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.Append(CsvHeader).Append('\n');
        foreach (var (template, variants) in snapshot)
        {
            foreach (var v in variants.OrderBy(x => x.Language, StringComparer.Ordinal))
            {
                sb.Append(EscapeCsv(template.Code)).Append(',');
                sb.Append(EscapeCsv(v.Language)).Append(',');
                sb.Append(EscapeCsv(v.SubjectOrTitle)).Append(',');
                sb.Append(EscapeCsv(v.Body)).Append(',');
                sb.Append(v.IsApproved ? "true" : "false").Append(',');
                sb.Append(EscapeCsv(v.TranslatorNote ?? string.Empty));
                sb.Append('\n');
            }
        }
        return Result<byte[]>.Success(new UTF8Encoding(false).GetBytes(sb.ToString()));
    }

    /// <inheritdoc />
    public async Task<Result<TemplateCatalogImportReportDto>> ImportXmlAsync(
        Stream xml,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument doc;
        try
        {
            doc = await XDocument.LoadAsync(xml, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (XmlException ex)
        {
            return Result<TemplateCatalogImportReportDto>.Failure(
                ErrorCodes.ImportValidationFailed,
                $"XML payload is not well-formed: {ex.Message}");
        }

        var rows = new List<ImportRow>();
        foreach (var templateEl in doc.Root?.Elements("Template") ?? Enumerable.Empty<XElement>())
        {
            var code = (string?)templateEl.Attribute("code");
            foreach (var variantEl in templateEl.Elements("Variant"))
            {
                rows.Add(new ImportRow(
                    TemplateCode: code ?? string.Empty,
                    Language: (string?)variantEl.Attribute("language") ?? string.Empty,
                    Subject: (string?)variantEl.Attribute("subject") ?? string.Empty,
                    Body: variantEl.Value, // CDATA content is exposed via .Value
                    Approved: string.Equals((string?)variantEl.Attribute("approved"), "true", StringComparison.OrdinalIgnoreCase),
                    TranslatorNote: (string?)variantEl.Attribute("translatorNote")));
            }
        }

        return await ApplyImportAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<TemplateCatalogImportReportDto>> ImportCsvAsync(
        Stream csv,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(csv);

        using var reader = new StreamReader(csv, new UTF8Encoding(false));
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            return Result<TemplateCatalogImportReportDto>.Failure(
                ErrorCodes.ImportValidationFailed,
                "CSV payload is empty.");
        }

        // RFC 4180 record-aware parsing — fields can contain embedded \n / \r when
        // they are wrapped in double-quotes. The single ParseCsvRecords helper walks
        // the whole payload at once instead of line-by-line splitting so a body
        // with line breaks round-trips correctly.
        var records = ParseCsvRecords(content);
        if (records.Count == 0)
        {
            return Result<TemplateCatalogImportReportDto>.Failure(
                ErrorCodes.ImportValidationFailed,
                "CSV payload contained no rows.");
        }

        var rows = new List<ImportRow>();
        // Skip the header row (records[0]); do not require an exact match — a
        // trailing space or different line endings shouldn't fail.
        for (var i = 1; i < records.Count; i++)
        {
            var fields = records[i];
            if (fields.Count == 1 && string.IsNullOrEmpty(fields[0]))
            {
                continue;
            }
            while (fields.Count < 6) fields.Add(string.Empty);
            rows.Add(new ImportRow(
                TemplateCode: fields[0],
                Language: fields[1],
                Subject: fields[2],
                Body: fields[3],
                Approved: string.Equals(fields[4], "true", StringComparison.OrdinalIgnoreCase),
                TranslatorNote: string.IsNullOrEmpty(fields[5]) ? null : fields[5]));
        }

        return await ApplyImportAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Common back-end for <see cref="ImportXmlAsync"/> and <see cref="ImportCsvAsync"/>.
    /// Validates every row, aborts on any error (all-or-nothing), upserts when
    /// every row passes, and emits the Critical <c>TEMPLATE.CATALOG.IMPORTED</c>
    /// audit row on success.
    /// </summary>
    /// <param name="rows">Parsed import rows.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Report on success; failure with <see cref="ErrorCodes.ImportValidationFailed"/> on validation errors.</returns>
    private async Task<Result<TemplateCatalogImportReportDto>> ApplyImportAsync(
        IReadOnlyList<ImportRow> rows,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var created = 0;
        var updated = 0;
        var skipped = 0;

        // Validate every row up-front. Unknown template codes are a WARNING (skip);
        // bad language / empty subject / oversized body are ERRORS (abort).
        var distinctCodes = rows.Select(r => r.TemplateCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var templatesByCode = await _db.DocumentTemplates
            .Where(t => t.IsActive && distinctCodes.Contains(t.Code))
            .ToDictionaryAsync(t => t.Code, StringComparer.OrdinalIgnoreCase, ct)
            .ConfigureAwait(false);

        var validRows = new List<(ImportRow Row, DocumentTemplate Template)>();
        var lineNumber = 1;
        foreach (var row in rows)
        {
            lineNumber++;
            if (!templatesByCode.TryGetValue(row.TemplateCode, out var template))
            {
                warnings.Add($"unknown template code '{row.TemplateCode}'");
                skipped++;
                continue;
            }
            if (!TemplateLanguages.All.Contains(row.Language))
            {
                errors.Add($"row {lineNumber}: unsupported language '{row.Language}'.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.Subject) || row.Subject.Length > 200)
            {
                errors.Add($"row {lineNumber}: subject must be 1..200 chars.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.Body) || row.Body.Length > 100_000)
            {
                errors.Add($"row {lineNumber}: body must be 1..100,000 chars.");
                continue;
            }
            validRows.Add((row, template));
        }

        if (errors.Count > 0)
        {
            var report = new TemplateCatalogImportReportDto(
                Created: 0,
                Updated: 0,
                Skipped: skipped,
                Warnings: warnings,
                Errors: errors);
            return Result<TemplateCatalogImportReportDto>.Failure(
                ErrorCodes.ImportValidationFailed,
                JsonSerializer.Serialize(report));
        }

        // All rows valid (or skipped) — proceed with the upsert pass.
        foreach (var (row, template) in validRows)
        {
            var existing = await _db.TemplateVariants
                .FirstOrDefaultAsync(
                    v => v.TemplateId == template.Id && v.Language == row.Language && v.IsActive,
                    ct)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.TemplateVariants.Add(new TemplateVariant
                {
                    TemplateId = template.Id,
                    Language = row.Language,
                    SubjectOrTitle = row.Subject,
                    Body = row.Body,
                    IsApproved = row.Approved,
                    TranslatorNote = row.TranslatorNote,
                    CreatedAtUtc = _clock.UtcNow,
                    CreatedBy = _caller.UserSqid,
                    IsActive = true,
                });
                created++;
            }
            else
            {
                existing.SubjectOrTitle = row.Subject;
                existing.Body = row.Body;
                existing.IsApproved = row.Approved;
                existing.TranslatorNote = row.TranslatorNote;
                existing.UpdatedAtUtc = _clock.UtcNow;
                existing.UpdatedBy = _caller.UserSqid;
                updated++;
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (_audit is not null)
        {
            var details = JsonSerializer.Serialize(new
            {
                Created = created,
                Updated = updated,
                Skipped = skipped,
            });
            await _audit.RecordAsync(
                eventCode: "TEMPLATE.CATALOG.IMPORTED",
                severity: AuditSeverity.Critical,
                actorId: _caller.UserSqid ?? "system",
                targetEntity: nameof(DocumentTemplate),
                targetEntityId: null,
                detailsJson: details,
                sourceIp: _caller.SourceIp,
                correlationId: _caller.CorrelationId,
                cancellationToken: ct).ConfigureAwait(false);
        }

        return Result<TemplateCatalogImportReportDto>.Success(
            new TemplateCatalogImportReportDto(created, updated, skipped, warnings, errors));
    }

    /// <summary>
    /// Loads the current catalog snapshot — every active template plus every active
    /// variant attached to it — into an in-memory tuple sequence sorted alphabetically
    /// by template code so the exported output is deterministic across runs.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sorted list of (template, variants) pairs.</returns>
    private async Task<IReadOnlyList<(DocumentTemplate Template, IReadOnlyList<TemplateVariant> Variants)>> LoadCatalogSnapshotAsync(
        CancellationToken ct)
    {
        var templatesRaw = await _db.DocumentTemplates
            .AsNoTracking()
            .Where(t => t.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        // Client-side ordering — the InMemory provider does not translate
        // OrderBy with a StringComparer; Postgres orders text ordinally by default
        // so the production behaviour matches the deterministic client-side sort.
        var templates = templatesRaw
            .OrderBy(t => t.Code, StringComparer.Ordinal)
            .ToList();

        var templateIds = templates.Select(t => t.Id).ToList();
        var variants = await _db.TemplateVariants
            .AsNoTracking()
            .Where(v => v.IsActive && templateIds.Contains(v.TemplateId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byTemplate = variants.GroupBy(v => v.TemplateId).ToDictionary(g => g.Key, g => (IReadOnlyList<TemplateVariant>)g.ToList());
        return templates
            .Select(t => (t, byTemplate.TryGetValue(t.Id, out var v) ? v : (IReadOnlyList<TemplateVariant>)Array.Empty<TemplateVariant>()))
            .ToList();
    }

    /// <summary>
    /// Quotes a field for RFC 4180 CSV. Returns the bare value when no escaping is
    /// needed (no comma, double-quote, or line break); otherwise wraps it in
    /// double-quotes and doubles any embedded quotes.
    /// </summary>
    /// <param name="value">Raw field value.</param>
    /// <returns>CSV-safe field.</returns>
    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(['\n', '\r', ',', '"']) < 0)
        {
            return value;
        }
        return string.Concat("\"", value.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }

    /// <summary>
    /// Parses an entire RFC 4180 CSV payload into a list-of-records-of-fields.
    /// Correctly handles double-quoted fields containing commas, line breaks
    /// (<c>\n</c>, <c>\r\n</c>), and doubled internal quotes. Trailing empty lines
    /// are ignored.
    /// </summary>
    /// <param name="content">Whole CSV payload as a string.</param>
    /// <returns>List of records, each a list of fields.</returns>
    private static List<List<string>> ParseCsvRecords(string content)
    {
        var records = new List<List<string>>();
        var current = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        void CommitField()
        {
            current!.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void CommitRecord()
        {
            // Suppress trivial empty records (caused by trailing newlines).
            if (current.Count == 1 && current[0].Length == 0)
            {
                current.Clear();
                return;
            }
            records.Add(current);
            current = new List<string>();
        }

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        // Doubled quote → literal quote.
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else
            {
                if (c == ',')
                {
                    CommitField();
                }
                else if (c == '\r')
                {
                    // CR or CRLF terminator — commit + skip the LF if present.
                    CommitField();
                    CommitRecord();
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                    {
                        i++;
                    }
                }
                else if (c == '\n')
                {
                    CommitField();
                    CommitRecord();
                }
                else if (c == '"' && !fieldStarted)
                {
                    inQuotes = true;
                    fieldStarted = true;
                }
                else
                {
                    field.Append(c);
                    fieldStarted = true;
                }
            }
        }
        // Flush any in-progress final field / record.
        if (fieldStarted || current.Count > 0 || field.Length > 0)
        {
            CommitField();
            CommitRecord();
        }
        return records;
    }

    /// <summary>
    /// Internal row shape used by both the XML and CSV parsing paths.
    /// </summary>
    /// <param name="TemplateCode">Stable kebab-case template code.</param>
    /// <param name="Language">Lower-case language code.</param>
    /// <param name="Subject">Translated subject/title.</param>
    /// <param name="Body">Translated body.</param>
    /// <param name="Approved">Approval flag as parsed from the input.</param>
    /// <param name="TranslatorNote">Optional translator note.</param>
    private sealed record ImportRow(
        string TemplateCode,
        string Language,
        string Subject,
        string Body,
        bool Approved,
        string? TranslatorNote);

}
