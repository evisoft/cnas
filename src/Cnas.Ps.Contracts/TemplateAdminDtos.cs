namespace Cnas.Ps.Contracts;

/// <summary>
/// UC17 — One row of the document-template catalog exposed by
/// <c>GET /api/templates</c> and <c>GET /api/templates/{code}</c>. The
/// <see cref="Code"/> is the stable, machine-readable identifier the rest of the API
/// uses to refer to a template (e.g. the renderer dispatch in
/// <c>DocumentGenerationService</c>); <see cref="ClrTypeFullName"/> and
/// <see cref="AssemblyName"/> carry the implementation-side coordinates so admins can
/// correlate a registered template back to its source code while triaging.
/// </summary>
/// <remarks>
/// <para>
/// <b>RULE 3 (Sqid) exception — documented per CLAUDE.md.</b> CLAUDE.md mandates that
/// every external identifier on a Contracts DTO be a Sqid-encoded <see cref="string"/>
/// rather than a raw <see cref="long"/> / <see cref="int"/>. The rule exists to prevent
/// leaking business intelligence (volume, growth rate) through <em>sequential surrogate
/// keys</em> — a third party who sees <c>/api/users/4523</c> and later <c>/api/users/4524</c>
/// learns that exactly one user was created between the two requests.
/// </para>
/// <para>
/// Template <see cref="Code"/> values are <em>not</em> sequential surrogate keys. They are
/// stable, human-meaningful kebab-case strings (<c>"refuz-aplicare"</c>,
/// <c>"decizia-pensie"</c>, <c>"aviz-final-control"</c>, ...) authored by the business and
/// pinned by <c>Cnas.Ps.Core.Common.ErrorCodes</c>-style contract discipline —
/// renaming a code is a breaking change. They reveal no volume signal (the count of
/// distinct codes does not grow with traffic; new templates land in deployments) and no
/// ordering signal (codes are not allocated; they are typed out by humans). Sqid-encoding
/// them would therefore add no security benefit while obscuring the very identifier the
/// downstream renderer needs to match.
/// </para>
/// <para>
/// For the same reason <see cref="ClrTypeFullName"/> and <see cref="AssemblyName"/> are
/// passed through as-is: they are implementation metadata, not database keys.
/// </para>
/// <para>
/// <b>Phase 2A backward-compatibility — optional fields.</b> Phase 2A extended the
/// catalog to include both DI-baked templates AND persistent operator-uploaded rows;
/// the latter need extra fields (<see cref="Source"/>, <see cref="Name"/>,
/// <see cref="Version"/>, <see cref="ContentLength"/>) that have no analogue on the
/// DI-baked side. We added them as <em>optional</em> positional-record parameters with
/// default values rather than introducing a sibling DTO so that existing JSON clients
/// (phase 1 front-ends, partner integrations testing the read-only surface) keep
/// deserialising unchanged — System.Text.Json ignores unknown fields by default, so
/// older clients see a DTO with the new fields populated but never read them. New
/// clients opt in to the extra fields by binding the full record. This is the standard
/// "add-only" evolution pattern for public contracts; never remove or reorder existing
/// parameters.
/// </para>
/// </remarks>
/// <param name="Code">
/// Stable template code (e.g. <c>refuz-aplicare</c>). The code is part of the API contract
/// — renaming a code is a breaking change. Codes are kebab-case and matched
/// case-insensitively by <c>Cnas.Ps.Application.UseCases.ITemplateAdminService.GetAsync</c>
/// but echoed back in their canonical lower-case form.
/// </param>
/// <param name="ClrTypeFullName">
/// Assembly-qualified name of the .NET type implementing the template (e.g.
/// <c>Cnas.Ps.Infrastructure.Documents.Templates.RefuzAplicareTemplate</c>). Used by
/// admins to navigate from the catalog to the source code. Empty string for persistent
/// rows (they have no compiled-in type — their behaviour comes from the uploaded DOCX).
/// </param>
/// <param name="AssemblyName">
/// Simple name of the assembly hosting the template implementation (e.g.
/// <c>Cnas.Ps.Infrastructure</c>). Empty string for persistent rows.
/// </param>
/// <param name="Source">
/// Origin of the catalog row. One of two literal values:
/// <list type="bullet">
///   <item><c>"DI"</c> — the row is a DI-baked <c>IDocxTemplate</c> singleton
///         compiled into the Infrastructure assembly. <see cref="ClrTypeFullName"/> and
///         <see cref="AssemblyName"/> point at the implementation type.</item>
///   <item><c>"Persistent"</c> — the row was uploaded by an operator and lives in the
///         <c>DocumentTemplates</c> table + MinIO. <see cref="Name"/>,
///         <see cref="Version"/>, and <see cref="ContentLength"/> are populated.</item>
/// </list>
/// When a code exists in BOTH registries the persistent row wins (operator-override
/// semantics) — see the service-layer XML doc for the rationale. Defaults to <c>"DI"</c>
/// for backward compatibility with phase 1 JSON clients that constructed the DTO with
/// only the first three positional parameters.
/// </param>
/// <param name="Name">
/// Human-readable display name for the persistent template. <see langword="null"/> for
/// DI-baked rows (the implementation type's class name serves the same purpose for
/// admins navigating from the catalog to the source code).
/// </param>
/// <param name="Version">
/// Monotonically increasing version number per <see cref="Code"/> for persistent rows.
/// <see langword="null"/> for DI-baked rows (their version is the code release).
/// </param>
/// <param name="ContentLength">
/// Size of the uploaded DOCX in bytes for persistent rows. <see langword="null"/> for
/// DI-baked rows. Exposed at list time so the admin UI can show "x KB" without
/// round-tripping to MinIO.
/// </param>
public sealed record TemplateCatalogEntry(
    string Code,
    string ClrTypeFullName,
    string AssemblyName,
    string Source = "DI",
    string? Name = null,
    int? Version = null,
    long? ContentLength = null);

/// <summary>
/// UC17 phase 2A — Streamed download of a persistent template's current binary. Returned
/// by <c>ITemplateAdminService.DownloadAsync</c> and shaped to drop directly into a
/// <c>FileStreamResult</c> on the HTTP boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stream ownership.</b> <see cref="Content"/> is OPEN when the record is returned;
/// the caller is responsible for disposing it (the controller wraps it in
/// <c>FileStreamResult</c>, which disposes it after the response body is written). The
/// service does not retain a reference, so the buffered bytes are not double-disposed.
/// </para>
/// <para>
/// <b>SHA-256 contract.</b> <see cref="Sha256"/> is the hex digest computed at UPLOAD
/// time and stored on the row. The download path does not re-verify by default — the
/// stream is handed to the caller as-is — but the value is included so a paranoid
/// caller can checksum the bytes off-band. Phase 2B may add server-side verification on
/// download if a tamper-detection requirement materialises.
/// </para>
/// </remarks>
/// <param name="Content">
/// Open, readable stream positioned at byte 0. Caller disposes (e.g. via
/// <c>FileStreamResult</c>'s framework lifetime).
/// </param>
/// <param name="ContentType">MIME type of the stream — always the DOCX MIME in phase 2A.</param>
/// <param name="ContentLength">Size of the binary in bytes — useful for the <c>Content-Length</c> response header.</param>
/// <param name="SuggestedFileName">
/// Server-suggested filename for the <c>Content-Disposition</c> header (e.g.
/// <c>decizia-pensie.docx</c>). Composed as <c>{code}.docx</c> so the download lands in
/// the operator's filesystem with a meaningful name.
/// </param>
/// <param name="Sha256">SHA-256 hex digest (lower-case, 64 chars) computed at upload time.</param>
public sealed record TemplateDownloadStream(
    Stream Content,
    string ContentType,
    long ContentLength,
    string SuggestedFileName,
    string Sha256);

/// <summary>
/// UC17 phase 2B — Request body for <c>POST /api/templates/{code}/render</c>. Carries the
/// dictionary of placeholder values the uploaded-template renderer substitutes into the
/// stored DOCX before streaming the result back to the caller.
/// </summary>
/// <remarks>
/// <para>
/// <b>String-only values.</b> The dictionary uses <see cref="string"/> values because the
/// uploaded template author has no compile-time contract to express type expectations —
/// the placeholder is just text in a Word document. Callers format dates, money, and
/// Sqid-encoded identifiers into strings BEFORE posting. This matches the
/// operator-authoring mental model documented on
/// <c>Cnas.Ps.Application.UseCases.IUploadedTemplateRenderer</c>.
/// </para>
/// <para>
/// <b>Unknown-placeholder contract.</b> Placeholders that appear in the stored DOCX but
/// have no entry in <see cref="Data"/> are LEFT VERBATIM in the rendered output — they
/// do not throw and do not produce a 400. This inherits the renderer's lenient
/// substitution contract so callers can supply partial dictionaries (e.g. for preview
/// rendering) without first having to scrape the template for required keys.
/// </para>
/// <para>
/// <b>Sqid not applicable.</b> The path parameter <c>code</c> is a stable kebab-case
/// template identifier (e.g. <c>refuz-aplicare</c>), not a sequential surrogate key —
/// see the matching exception documented on <see cref="TemplateCatalogEntry"/>.
/// </para>
/// </remarks>
/// <param name="Data">
/// Placeholder values keyed by placeholder name. <see langword="null"/> is treated as an
/// empty dictionary (every placeholder in the template is left verbatim). Case-sensitive
/// lookup — <c>{{Name}}</c> matches the dictionary key <c>Name</c> exactly, not
/// <c>name</c>.
/// </param>
public sealed record RenderUploadedTemplateRequest(
    IReadOnlyDictionary<string, string>? Data);
