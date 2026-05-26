using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Templates;

/// <summary>
/// R0134 / TOR CF 17.17 — Admin XML / CSV import-export port for the template-catalog
/// metadata. Export streams the current template + variant rows; import re-applies an
/// edited catalog (typically the result of a translation round-trip in a desktop
/// translation tool). Imports are upsert-only for VARIANT rows of EXISTING templates;
/// unknown template codes are skipped with a warning — the import never creates new
/// templates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Port abstraction instead of a Service.</b> XML and CSV are
/// interchange formats; the application layer cares about the round-trip semantics,
/// not the specific serialiser. The interface lives in the Application layer so the
/// Infrastructure implementation can be swapped (e.g. for a future Apache POI-style
/// XLSX shape) without touching the consumer.
/// </para>
/// <para>
/// <b>All-or-nothing import semantics.</b> The importer accumulates per-row
/// validation errors and warnings as it scans the input. If any row fails validation
/// (bad language, missing required field, etc.) the import is ABORTED before any
/// SaveChanges call — every row that would have been written is rolled back. The
/// returned failure carries the populated report so the admin sees every failing row
/// at once instead of fixing them one batch at a time.
/// </para>
/// <para>
/// <b>Audit emission.</b> A successful import emits a single Critical audit row
/// <c>TEMPLATE.CATALOG.IMPORTED</c> whose detail JSON carries the
/// (<c>Created</c>, <c>Updated</c>, <c>Skipped</c>) totals; per-row audit events are
/// intentionally suppressed to avoid pathological audit-log growth on a 100-template
/// catalog import. Aborted imports do NOT emit the audit row because no rows landed.
/// </para>
/// </remarks>
public interface ITemplateCatalogPort
{
    /// <summary>
    /// Exports the entire template catalog (templates + variants metadata) as a
    /// UTF-8 XML byte stream. The schema is <c>&lt;TemplateCatalog&gt;</c> root,
    /// child <c>&lt;Template code="…" defaultLanguage="ro"&gt;</c>, nested
    /// <c>&lt;Variant language="…" subject="…" approved="true"&gt;</c> with the
    /// body wrapped in a <c>&lt;![CDATA[...]]&gt;</c> block to preserve line breaks
    /// and reserved-character payloads. The DOCX blob is intentionally NOT included
    /// — only the metadata + plain-text body.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the serialised XML bytes; failure
    /// only when an underlying DB error occurs.
    /// </returns>
    Task<Result<byte[]>> ExportXmlAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports the catalog as a UTF-8 CSV byte stream with header row
    /// <c>TemplateCode,Language,Subject,Body,Approved,TranslatorNote</c>. One row per
    /// template-variant pair. Field values are RFC 4180 quoted whenever they contain
    /// a comma, double-quote, or line break; embedded double-quotes are doubled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The CSV bytes wrapped in a <see cref="Result{T}"/>.</returns>
    Task<Result<byte[]>> ExportCsvAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports an XML catalog produced by <see cref="ExportXmlAsync"/> (or an edited
    /// derivative). The caller passes a forward-only stream positioned at byte 0.
    /// Unknown template codes are skipped with a warning; existing templates have
    /// their variants upserted. The whole operation is all-or-nothing on validation
    /// errors.
    /// </summary>
    /// <param name="xml">UTF-8 XML stream positioned at byte 0.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Success(T)"/> wrapping the import report on success;
    /// <see cref="Result{T}.Failure(string, string)"/> with
    /// <c>IMPORT_VALIDATION_FAILED</c> on any validation error (carrying the report
    /// in the failure path is the responsibility of the implementation — the
    /// failure message includes a summary).
    /// </returns>
    Task<Result<TemplateCatalogImportReportDto>> ImportXmlAsync(
        Stream xml,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a CSV catalog produced by <see cref="ExportCsvAsync"/> (or an edited
    /// derivative). Same semantics as <see cref="ImportXmlAsync"/>; see the remarks
    /// on <see cref="ITemplateCatalogPort"/> for the import contract.
    /// </summary>
    /// <param name="csv">UTF-8 CSV stream positioned at byte 0.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The import report on success; a validation-failure result on errors.</returns>
    Task<Result<TemplateCatalogImportReportDto>> ImportCsvAsync(
        Stream csv,
        CancellationToken cancellationToken = default);
}
