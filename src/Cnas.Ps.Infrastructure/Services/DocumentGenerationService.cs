using System.Globalization;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Documents.Templates;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using DocumentEntity = Cnas.Ps.Core.Domain.Document;
using QuestPdfDocument = QuestPDF.Fluent.Document;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// UC08 — Generates the two PDF documents that accompany an examined dossier:
/// <em>Fișa de calcul</em> (calculation sheet) and <em>Decizia</em> (decision document).
/// Re-evaluates the configured <see cref="IDecisionEngine"/> against the application's
/// form payload to populate the rendered tables — the engine remains the sole source of
/// truth for eligibility and benefit amount (CLAUDE.md RULE 6).
/// </summary>
/// <remarks>
/// <para>
/// The PDF rendering uses QuestPDF when its native dependencies are available (Linux x64 /
/// Windows x64 production hosts). On dev runtimes that lack the native binaries (e.g.
/// <c>win-arm64</c>), the service falls back to a hand-rolled minimal PDF emitter that
/// satisfies the PDF magic-byte contract — mirroring the pattern in
/// <see cref="ReportingService"/>.
/// </para>
/// <para>
/// External identifiers are always Sqid-encoded (CLAUDE.md RULE 3). All timestamps go
/// through <see cref="ICnasTimeProvider"/> (CLAUDE.md UTC Everywhere).
/// </para>
/// </remarks>
public sealed class DocumentGenerationService : IDocumentGenerationService
{
    /// <summary>Bucket where generated documents are stored.</summary>
    private const string DocumentsBucket = "cnas-documents";

    /// <summary>MIME type emitted by this service for PDF renders.</summary>
    private const string PdfMimeType = "application/pdf";

    /// <summary>MIME type emitted by this service for DOCX renders.</summary>
    private const string DocxMimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>Audit event code emitted when a generation step completes.</summary>
    private const string AuditEventGenerated = "DOCUMENT.GENERATED";

    /// <summary>QuestPDF probe cache — shared semantics with <see cref="ReportingService"/>.</summary>
    private static bool? _questPdfAvailable;

    /// <summary>Synchronises the one-shot QuestPDF probe across concurrent first calls.</summary>
    private static readonly System.Threading.Lock QuestPdfLock = new();

    private readonly ICnasDbContext _db;
    private readonly ISqidService _sqids;
    private readonly ICnasTimeProvider _clock;
    private readonly ICallerContext _caller;
    private readonly IFileStorage _storage;
    private readonly IAuditService _audit;
    private readonly IDecisionEngine _engine;
    private readonly ILogger<DocumentGenerationService> _logger;

    /// <summary>
    /// Annex 7 DOCX templates, keyed by their case-insensitive <see cref="IDocxTemplate.TemplateCode"/>.
    /// Empty when the host hasn't registered any template implementations — in that case the
    /// service always falls back to <see cref="BuildDocx"/>.
    /// </summary>
    private readonly IReadOnlyDictionary<string, IDocxTemplate> _templates;

    /// <summary>
    /// UC17 phase 2B — Fallback renderer for operator-uploaded persistent templates.
    /// Consumed by <see cref="GenerateFromUploadedTemplateAsync"/>; may be
    /// <see langword="null"/> in legacy test wirings that predate phase 2B (in which
    /// case the uploaded-template overload returns <see cref="ErrorCodes.NotFound"/>
    /// uniformly).
    /// </summary>
    private readonly IUploadedTemplateRenderer? _uploadedRenderer;

    /// <summary>
    /// R0131 / CF 17.15 — metadata-driven validation gate applied before rendering an
    /// uploaded template. May be <see langword="null"/> in legacy test wirings; absence
    /// is treated as "no gate configured" so existing callers continue to work
    /// unchanged.
    /// </summary>
    private readonly ITemplateValidationService? _validationService;

    /// <summary>
    /// Wires the service with its collaborators. All arguments are required; null values
    /// throw <see cref="ArgumentNullException"/>.
    /// </summary>
    /// <param name="db">EF Core context abstraction.</param>
    /// <param name="sqids">Sqid encoder/decoder.</param>
    /// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="caller">Authenticated caller context (audit identity).</param>
    /// <param name="storage">File-storage facade (MinIO).</param>
    /// <param name="audit">Audit journal façade.</param>
    /// <param name="engine">Decision engine used to re-evaluate the application outcome.</param>
    /// <param name="logger">Structured logger.</param>
    /// <param name="templates">
    /// Optional Annex 7 DOCX templates. When a render request specifies a template code
    /// matching one of these, the dispatcher routes the render to it; otherwise the
    /// generic <see cref="BuildDocx"/> path is used. Pass <see langword="null"/> or an
    /// empty enumerable to disable templated rendering entirely.
    /// </param>
    /// <param name="uploadedRenderer">
    /// UC17 phase 2B — Optional fallback renderer for operator-uploaded persistent
    /// templates. Consumed by <see cref="GenerateFromUploadedTemplateAsync"/>. When
    /// omitted (legacy test wirings), the uploaded-template overload uniformly
    /// returns <see cref="ErrorCodes.NotFound"/>. Production composition wires the
    /// real <c>UploadedTemplateRenderer</c> scoped instance.
    /// </param>
    /// <param name="validationService">
    /// R0131 / CF 17.15 — Optional metadata-driven validation gate. When supplied
    /// and the addressed template carries a non-null
    /// <see cref="DocumentTemplate.ValidationRulesJson"/>, every rule is applied
    /// against the supplied form values; the first failure short-circuits the render
    /// with <see cref="ErrorCodes.TemplateValidationFailed"/>. Absent (legacy test
    /// wirings) ⇒ gate skipped, preserving back-compat.
    /// </param>
    public DocumentGenerationService(
        ICnasDbContext db,
        ISqidService sqids,
        ICnasTimeProvider clock,
        ICallerContext caller,
        IFileStorage storage,
        IAuditService audit,
        IDecisionEngine engine,
        ILogger<DocumentGenerationService> logger,
        IEnumerable<IDocxTemplate>? templates = null,
        IUploadedTemplateRenderer? uploadedRenderer = null,
        ITemplateValidationService? validationService = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _sqids = sqids;
        _clock = clock;
        _caller = caller;
        _storage = storage;
        _audit = audit;
        _engine = engine;
        _logger = logger;
        _uploadedRenderer = uploadedRenderer;
        _validationService = validationService;

        // Build a case-insensitive lookup. Duplicate codes are reduced to the last one
        // wins — registration order is therefore meaningful but the production
        // composition root registers each template exactly once.
        var map = new Dictionary<string, IDocxTemplate>(StringComparer.OrdinalIgnoreCase);
        if (templates is not null)
        {
            foreach (var t in templates)
            {
                if (t is null || string.IsNullOrWhiteSpace(t.TemplateCode))
                {
                    continue;
                }

                map[t.TemplateCode] = t;
            }
        }

        _templates = map;
    }

    /// <inheritdoc />
    public Task<Result<string>> GenerateCalculationSheetAsync(string dossierId, CancellationToken cancellationToken = default)
        => GenerateCalculationSheetAsync(dossierId, DocumentRenderFormat.Pdf, cancellationToken);

    /// <inheritdoc />
    public Task<Result<string>> GenerateDecisionAsync(string dossierId, CancellationToken cancellationToken = default)
        => GenerateDecisionAsync(dossierId, DocumentRenderFormat.Pdf, cancellationToken);

    /// <inheritdoc />
    public Task<Result<string>> GenerateCalculationSheetAsync(
        string dossierId,
        DocumentRenderFormat format,
        CancellationToken cancellationToken = default)
        => GenerateAsync(dossierId, isDecision: false, format, cancellationToken);

    /// <inheritdoc />
    public Task<Result<string>> GenerateDecisionAsync(
        string dossierId,
        DocumentRenderFormat format,
        CancellationToken cancellationToken = default)
        => GenerateAsync(dossierId, isDecision: true, format, cancellationToken);

    /// <inheritdoc />
    public async Task<Result<byte[]>> GenerateFromUploadedTemplateAsync(
        string templateCode,
        IReadOnlyDictionary<string, string>? data,
        CancellationToken cancellationToken = default)
    {
        // The renderer collaborator is optional on the constructor (legacy test
        // wirings predate phase 2B). Treat absence as "no fallback configured" and
        // surface NotFound so callers get a clean failure rather than a NRE.
        if (_uploadedRenderer is null)
        {
            return Result<byte[]>.Failure(
                ErrorCodes.NotFound,
                "Uploaded-template renderer is not configured.");
        }

        // Normalise the data dictionary so the renderer never sees null — a null
        // input is operator-friendly shorthand for "no values supplied, leave
        // every placeholder verbatim", and forcing every caller to allocate an
        // empty dictionary would be noise.
        var safeData = data ?? new Dictionary<string, string>(StringComparer.Ordinal);

        // R0131 / CF 17.15 — apply the metadata-driven validation gate before
        // touching MinIO. When the gate is not configured (legacy test wirings)
        // OR the template has no rules persisted, ValidateAsync returns success
        // and the render proceeds unchanged.
        if (_validationService is not null)
        {
            var asNullable = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var kv in safeData)
            {
                asNullable[kv.Key] = kv.Value;
            }
            var validationResult = await _validationService
                .ValidateAsync(templateCode, asNullable, cancellationToken)
                .ConfigureAwait(false);
            if (validationResult.IsFailure)
            {
                return Result<byte[]>.Failure(
                    validationResult.ErrorCode!,
                    validationResult.ErrorMessage!);
            }
        }

        // CanRenderAsync is a cheap existence probe (single COUNT(*)-style query);
        // gates the more-expensive blob fetch and substitution path. If a row
        // exists, hand the work off to the renderer and return its result
        // verbatim — the renderer already classifies failures using the same
        // stable ErrorCodes that the rest of the service surface uses.
        if (!await _uploadedRenderer.CanRenderAsync(templateCode, cancellationToken).ConfigureAwait(false))
        {
            return Result<byte[]>.Failure(
                ErrorCodes.NotFound,
                $"No persistent template registered with code '{templateCode}'.");
        }

        return await _uploadedRenderer.RenderAsync(templateCode, safeData, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared body for both generation methods. Loads the dossier graph, re-evaluates the
    /// decision engine, renders the document, uploads it, and persists the
    /// <see cref="Cnas.Ps.Core.Domain.Document"/> row.
    /// </summary>
    /// <param name="dossierId">Sqid-encoded dossier id.</param>
    /// <param name="isDecision">
    /// <see langword="true"/> to render the Decizia; <see langword="false"/> to render the
    /// Fișa de calcul.
    /// </param>
    /// <param name="format">PDF (legacy) or DOCX (Annex 7).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<Result<string>> GenerateAsync(
        string dossierId,
        bool isDecision,
        DocumentRenderFormat format,
        CancellationToken cancellationToken)
    {
        // 1. Decode Sqid → internal id (CLAUDE.md RULE 3).
        var decoded = _sqids.TryDecode(dossierId);
        if (decoded.IsFailure)
        {
            return Result<string>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        // 2. Load dossier + application + service passport in a single round trip.
        var dossier = await _db.Dossiers
            .Include(d => d.Application)
                .ThenInclude(a => a!.Solicitant)
            .SingleOrDefaultAsync(d => d.Id == decoded.Value && d.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (dossier is null || dossier.Application is null)
        {
            return Result<string>.Failure(ErrorCodes.NotFound, "Dossier not found.");
        }

        var passport = await _db.ServicePassports
            .SingleOrDefaultAsync(p => p.Id == dossier.Application.ServicePassportId && p.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (passport is null)
        {
            return Result<string>.Failure(ErrorCodes.NotFound, "Service passport not found.");
        }

        // 3. Re-evaluate the decision engine. We do this so the rendered document reflects
        //    the same outcome the application-processing pipeline saw on intake. The engine
        //    is pure and idempotent — calling it twice with the same inputs is safe.
        var claimDate = dossier.Application.SubmittedAtUtc ?? _clock.UtcNow;
        var factsResult = FormPayloadParser.Parse(dossier.Application.FormPayloadJson, claimDate);
        // When the form payload cannot be parsed we still produce a document — it will show
        // a "n/a" outcome rather than fail closed. This mirrors the user-facing behaviour
        // of the application-processing service which auto-rejects rather than blocking.
        DecisionOutcome? outcome = null;
        if (factsResult.IsSuccess)
        {
            var evalResult = _engine.Evaluate(passport.DecisionRulesJson, factsResult.Value);
            if (evalResult.IsSuccess)
            {
                outcome = evalResult.Value;
            }
        }

        var snapshot = new DossierSnapshot(
            DossierNumber: dossier.DossierNumber,
            ApplicantNationalId: dossier.Application.Solicitant?.NationalId ?? "—",
            ApplicantDisplayName: dossier.Application.Solicitant?.DisplayName ?? "—",
            PassportCode: passport.Code,
            PassportName: passport.NameRo,
            ApplicationReference: dossier.Application.ReferenceNumber ?? _sqids.Encode(dossier.Application.Id),
            ApplicationSubmittedAtUtc: dossier.Application.SubmittedAtUtc,
            Outcome: outcome,
            GeneratedAtUtc: _clock.UtcNow);

        // 4. Render bytes — PDF via QuestPDF (with minimal-PDF fallback) or DOCX via OpenXML.
        byte[] documentBytes;
        string mimeType;
        string extension;
        if (format == DocumentRenderFormat.Docx)
        {
            var title = isDecision
                ? $"Decizia nr. {snapshot.DossierNumber}"
                : "Fișa de calcul";
            var lines = isDecision
                ? BuildDecisionLines(snapshot)
                : BuildCalculationSheetLines(snapshot);

            // ── Template routing (Annex 7). ──
            // Each render call has a canonical Annex 7 template code: "decizia-pensie"
            // for the Decizia, "fisa-de-calcul" for the Fișa de calcul. If a registered
            // IDocxTemplate matches that code, route to it; otherwise fall back to the
            // generic in-class BuildDocx so the system still produces a valid DOCX even
            // before all templates have been implemented.
            var templateCode = isDecision
                ? DeciziaPensieTemplate.Code
                : FisaDeCalculTemplate.Code;
            documentBytes = TryRenderWithTemplate(templateCode, snapshot, lines)
                ?? BuildDocx(title, snapshot, lines);
            mimeType = DocxMimeType;
            extension = "docx";
        }
        else
        {
            documentBytes = isDecision
                ? RenderDecisionPdf(snapshot)
                : RenderCalculationSheetPdf(snapshot);
            mimeType = PdfMimeType;
            extension = "pdf";
        }

        // 5. Upload to MinIO (cnas-documents bucket).
        await using var docStream = new MemoryStream(documentBytes, writable: false);
        var stored = await _storage.PutAsync(DocumentsBucket, docStream, mimeType, cancellationToken)
            .ConfigureAwait(false);
        if (stored.IsFailure)
        {
            return Result<string>.Failure(stored.ErrorCode!, stored.ErrorMessage!);
        }

        // 6. Persist Document row.
        // NOTE: There is no DocumentKind.CalculationSheet enum value yet — we reuse
        // DocumentKind.Information for the Fișa de calcul. The Decizia uses DocumentKind.Decision
        // which is already defined. Adding a dedicated CalculationSheet kind is deferred so we
        // don't churn existing reports / dashboards that key on the current enum.
        var fileName = isDecision
            ? $"Decizia_{dossier.DossierNumber}.{extension}"
            : $"Fisa-de-calcul_{dossier.DossierNumber}.{extension}";

        var doc = new DocumentEntity
        {
            DossierId = dossier.Id,
            Kind = isDecision ? DocumentKind.Decision : DocumentKind.Information,
            // The Document entity has a single human-readable identifier (Title); we put the
            // canonical filename there so downstream presigned-download flows surface a sensible
            // name without requiring an extra column.
            Title = fileName,
            MimeType = mimeType,
            SizeBytes = stored.Value.SizeBytes,
            StorageObjectKey = stored.Value.ObjectKey,
            StorageBucket = DocumentsBucket,
            ContentSha256Hex = stored.Value.ContentSha256Hex,
            CreatedAtUtc = _clock.UtcNow,
            CreatedBy = _caller.UserSqid ?? "system",
            IsActive = true,
        };
        _db.Documents.Add(doc);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var documentSqid = _sqids.Encode(doc.Id);

        // 7. Audit (Notice — write to non-sensitive data).
        var detailsJson = JsonSerializer.Serialize(new
        {
            dossierId = _sqids.Encode(dossier.Id),
            kind = isDecision ? "Decision" : "CalculationSheet",
            documentId = documentSqid,
        });
        await _audit.RecordAsync(
            AuditEventGenerated,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "system",
            // Hard-coded "Document" — nameof(DocumentEntity) would return the alias name
            // and break audit-log correlation with other services that use nameof(Document).
            "Document",
            doc.Id,
            detailsJson,
            _caller.SourceIp,
            _caller.CorrelationId,
            cancellationToken).ConfigureAwait(false);

        return Result<string>.Success(documentSqid);
    }

    /// <summary>
    /// Renders the Fișa de calcul (calculation sheet) PDF: header with applicant block,
    /// then a table listing each fact, the engine's reason codes, and the computed amount.
    /// </summary>
    private byte[] RenderCalculationSheetPdf(DossierSnapshot snapshot)
    {
        var lines = BuildCalculationSheetLines(snapshot);

        if (!TryInitializeQuestPdf())
        {
            return RenderMinimalPdf(lines);
        }

        try
        {
            return RenderQuestPdf("Fișa de calcul", snapshot, lines);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "QuestPDF calculation-sheet render failed; falling back to minimal PDF.");
            return RenderMinimalPdf(lines);
        }
    }

    /// <summary>
    /// Renders the Decizia (decision document) PDF: header, dossier-number title, body
    /// stating the outcome, computed amount if eligible (or reason codes if not), and a
    /// signature/generation footer.
    /// </summary>
    private byte[] RenderDecisionPdf(DossierSnapshot snapshot)
    {
        var lines = BuildDecisionLines(snapshot);

        if (!TryInitializeQuestPdf())
        {
            return RenderMinimalPdf(lines);
        }

        try
        {
            return RenderQuestPdf($"Decizia nr. {snapshot.DossierNumber}", snapshot, lines);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "QuestPDF decision render failed; falling back to minimal PDF.");
            return RenderMinimalPdf(lines);
        }
    }

    /// <summary>
    /// Builds the list of <c>(label, value)</c> text rows displayed on the Fișa de calcul.
    /// </summary>
    private static IReadOnlyList<(string Field, string Value)> BuildCalculationSheetLines(DossierSnapshot s)
    {
        var rows = new List<(string, string)>
        {
            ("Dosar", s.DossierNumber),
            ("Solicitant", s.ApplicantDisplayName),
            ("Cod național (IDNP/IDNO)", s.ApplicantNationalId),
            ("Cod serviciu", s.PassportCode),
            ("Denumire serviciu", s.PassportName),
            ("Nr. cerere", s.ApplicationReference),
            (
                "Depusă la",
                s.ApplicationSubmittedAtUtc?.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                ?? "—"),
        };

        if (s.Outcome is not null)
        {
            rows.Add(("Eligibilitate", s.Outcome.IsEligible ? "Eligibil" : "Neeligibil"));
            rows.Add((
                "Sumă calculată",
                s.Outcome.Amount is { } amount
                    ? FormatMoney(amount)
                    : "—"));
            rows.Add((
                "Coduri motiv",
                s.Outcome.ReasonCodes.Count == 0 ? "—" : string.Join(", ", s.Outcome.ReasonCodes)));
        }
        else
        {
            rows.Add(("Eligibilitate", "n/a"));
            rows.Add(("Sumă calculată", "n/a"));
            rows.Add(("Coduri motiv", "n/a"));
        }

        rows.Add((
            "Generat la",
            s.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));

        return rows;
    }

    /// <summary>
    /// Builds the list of paragraph lines that compose the Decizia body.
    /// </summary>
    private static IReadOnlyList<(string Field, string Value)> BuildDecisionLines(DossierSnapshot s)
    {
        var submittedAt = s.ApplicationSubmittedAtUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ?? "—";
        var eligible = s.Outcome?.IsEligible ?? false;
        var decisionLine = eligible
            ? "ACORDAREA prestației"
            : "RESPINGEREA cererii";

        var rows = new List<(string, string)>
        {
            ("Dosar", s.DossierNumber),
            ("Solicitant", s.ApplicantDisplayName),
            ("Cod național", s.ApplicantNationalId),
            ("Serviciu", $"{s.PassportCode} — {s.PassportName}"),
            (
                "Hotărâre",
                $"În urma examinării cererii nr. {s.ApplicationReference} a domnului/doamnei "
                + $"{s.ApplicantDisplayName}, depusă la {submittedAt}, se hotărăște: {decisionLine}."),
        };

        if (eligible && s.Outcome?.Amount is { } amount)
        {
            rows.Add(("Sumă acordată", FormatMoney(amount)));
        }
        else if (!eligible && s.Outcome is not null && s.Outcome.ReasonCodes.Count > 0)
        {
            rows.Add(("Motive respingere", string.Join(", ", s.Outcome.ReasonCodes)));
        }

        rows.Add(("Semnătură", "[Director CNAS]"));
        rows.Add((
            "Generat automat la",
            s.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));

        return rows;
    }

    /// <summary>Format a <see cref="Money"/> value using invariant culture.</summary>
    private static string FormatMoney(Money m) =>
        string.Create(CultureInfo.InvariantCulture, $"{m.Amount:0.00} {m.CurrencyCode}");

    /// <summary>
    /// Renders the supplied rows as a single-page A4 PDF via QuestPDF. Throws if the
    /// native dependencies are not loaded — callers are responsible for the fallback.
    /// </summary>
    private static byte[] RenderQuestPdf(
        string title,
        DossierSnapshot snapshot,
        IReadOnlyList<(string Field, string Value)> rows)
    {
        var document = QuestPdfDocument.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(25);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text("CNAS — Casa Națională de Asigurări Sociale").Bold().FontSize(11);
                    col.Item().Text(title).Bold().FontSize(14);
                    col.Item().Text($"Dosar {snapshot.DossierNumber}").FontSize(10);
                });

                page.Content().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(2);
                        cols.RelativeColumn(5);
                    });

                    table.Header(header =>
                    {
                        header.Cell().BorderBottom(1).PaddingVertical(2).Text("Câmp").SemiBold();
                        header.Cell().BorderBottom(1).PaddingVertical(2).Text("Valoare").SemiBold();
                    });

                    foreach (var (field, value) in rows)
                    {
                        table.Cell().PaddingVertical(1).Text(field);
                        table.Cell().PaddingVertical(1).Text(value);
                    }
                });

                page.Footer().AlignRight().Text(text =>
                {
                    text.Span("Pagina ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

        using var ms = new MemoryStream();
        document.GeneratePdf(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Emits a syntactically-valid PDF 1.4 document carrying the supplied rows as Helvetica
    /// text on a single A4 page. Used as the fallback when QuestPDF's native dependencies
    /// fail to load on the current runtime. Mirrors the pattern in
    /// <see cref="ReportingService"/> so callers don't need to inspect the byte stream to
    /// know which path produced it — both render valid PDF magic bytes.
    /// </summary>
    private static byte[] RenderMinimalPdf(IReadOnlyList<(string Field, string Value)> rows)
    {
        var lines = new List<string>(rows.Count + 2)
        {
            "CNAS — Casa Nationala de Asigurari Sociale",
            string.Empty,
        };
        lines.AddRange(rows.Select(r => $"{r.Field}: {r.Value}"));

        var content = new StringBuilder();
        content.Append("BT\n/F1 10 Tf\n");
        var y = 800f;
        foreach (var line in lines)
        {
            content.Append(CultureInfo.InvariantCulture, $"1 0 0 1 36 {y:0.##} Tm (");
            content.Append(EscapePdfString(line));
            content.Append(") Tj\n");
            y -= 14f;
            if (y < 36f) break;
        }
        content.Append("ET\n");
        var contentBytes = Encoding.ASCII.GetBytes(content.ToString());

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        var offsets = new List<long>();
        void WriteObj(string body)
        {
            offsets.Add(ms.Position);
            writer.Write(Encoding.ASCII.GetBytes(body));
        }

        writer.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"));

        WriteObj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        WriteObj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        WriteObj(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");

        offsets.Add(ms.Position);
        var header = $"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n";
        writer.Write(Encoding.ASCII.GetBytes(header));
        writer.Write(contentBytes);
        writer.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));

        WriteObj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var xrefOffset = ms.Position;
        var xref = new StringBuilder();
        xref.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var off in offsets)
        {
            xref.Append(CultureInfo.InvariantCulture, $"{off:D10} 00000 n \n");
        }
        xref.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n");
        xref.Append(CultureInfo.InvariantCulture, $"{xrefOffset}\n%%EOF\n");
        writer.Write(Encoding.ASCII.GetBytes(xref.ToString()));
        writer.Flush();

        return ms.ToArray();
    }

    /// <summary>Escape a string for inclusion as a PDF literal.</summary>
    private static string EscapePdfString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '(' || ch == ')' || ch == '\\') { sb.Append('\\').Append(ch); }
            else if (ch < 32 || ch > 126) { sb.Append('?'); }
            else { sb.Append(ch); }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Probes QuestPDF support once per process and caches the result. Returns
    /// <see langword="false"/> on runtimes where native deps are missing so callers can
    /// fall back to the minimal PDF emitter.
    /// </summary>
    private static bool TryInitializeQuestPdf()
    {
        if (_questPdfAvailable.HasValue) return _questPdfAvailable.Value;
        lock (QuestPdfLock)
        {
            if (_questPdfAvailable.HasValue) return _questPdfAvailable.Value;
            try
            {
                QuestPDF.Settings.License = LicenseType.Community;
                _questPdfAvailable = true;
            }
            catch (Exception)
            {
                _questPdfAvailable = false;
            }
            return _questPdfAvailable.Value;
        }
    }

    /// <summary>
    /// Attempts to render the requested Annex 7 template, returning the bytes on success
    /// or <see langword="null"/> when:
    /// <list type="bullet">
    ///   <item>No template is registered for the supplied <paramref name="templateCode"/>.</item>
    ///   <item>The template returned <see cref="Result{T}.Failure(string, string)"/>.</item>
    /// </list>
    /// In both null-returning cases, the caller falls back to the generic
    /// <see cref="BuildDocx"/> path.
    /// </summary>
    /// <param name="templateCode">Case-insensitive template identifier.</param>
    /// <param name="snapshot">Dossier snapshot used to populate facts.</param>
    /// <param name="lines">Pre-built (label, value) lines — included for the generic fallback only.</param>
    /// <returns>DOCX bytes on success; <see langword="null"/> otherwise.</returns>
    private byte[]? TryRenderWithTemplate(
        string templateCode,
        DossierSnapshot snapshot,
        IReadOnlyList<(string Field, string Value)> lines)
    {
        // Lines are intentionally unused inside this helper — the template builds its own
        // layout from facts. Kept on the signature so future templates can fall back to
        // the generic key/value rendering inline without a method-signature churn.
        _ = lines;

        if (!_templates.TryGetValue(templateCode, out var template))
        {
            return null;
        }

        var facts = BuildTemplateFacts(templateCode, snapshot);
        var result = template.Render(facts);
        if (result.IsFailure)
        {
            _logger.LogWarning(
                "Annex 7 template {TemplateCode} reported {ErrorCode}: {ErrorMessage}; falling back to generic DOCX.",
                templateCode,
                result.ErrorCode,
                result.ErrorMessage);
            return null;
        }

        return result.Value;
    }

    /// <summary>
    /// Translates a <see cref="DossierSnapshot"/> into the camelCase fact dictionary the
    /// Annex 7 templates expect. Each branch knows the required-fact contract of its
    /// template — kept inline to keep the routing single-file and easy to audit.
    /// </summary>
    /// <param name="templateCode">Canonical template code.</param>
    /// <param name="snapshot">Dossier snapshot.</param>
    /// <returns>Facts dictionary keyed by camelCase field name.</returns>
    private Dictionary<string, object?> BuildTemplateFacts(string templateCode, DossierSnapshot snapshot)
    {
        // Both currently-routed templates share the same minimal set of facts. The
        // additional templates (dispoziție, invitație, refuz) are not addressable from
        // this auto-generation path — they are rendered by their own callers downstream
        // (UC08 supports the two below; the others are produced by workflow-engine
        // services that already build their own fact dictionaries and call the
        // IDocxTemplate directly).
        if (string.Equals(templateCode, DeciziaPensieTemplate.Code, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["beneficiaryIdnp"] = snapshot.ApplicantNationalId,
                ["beneficiaryFullName"] = snapshot.ApplicantDisplayName,
                ["beneficiaryAddress"] = "—",
                ["serviceCode"] = snapshot.PassportCode,
                ["serviceTitleRo"] = snapshot.PassportName,
                ["grantedFromUtc"] = snapshot.ApplicationSubmittedAtUtc ?? snapshot.GeneratedAtUtc,
                ["monthlyAmountMdl"] = snapshot.Outcome?.Amount?.Amount ?? 0m,
            };
        }

        if (string.Equals(templateCode, FisaDeCalculTemplate.Code, StringComparison.OrdinalIgnoreCase))
        {
            var calcFacts = new Dictionary<string, string>
            {
                ["Dosar"] = snapshot.DossierNumber,
                ["Nr. cerere"] = snapshot.ApplicationReference,
                ["Eligibilitate"] = snapshot.Outcome?.IsEligible == true ? "Eligibil" : "Neeligibil",
                ["Coduri motiv"] = snapshot.Outcome?.ReasonCodes.Count > 0
                    ? string.Join(", ", snapshot.Outcome.ReasonCodes)
                    : "—",
            };

            return new Dictionary<string, object?>
            {
                ["beneficiaryIdnp"] = snapshot.ApplicantNationalId,
                ["beneficiaryFullName"] = snapshot.ApplicantDisplayName,
                ["serviceCode"] = snapshot.PassportCode,
                ["calculationFacts"] = calcFacts,
                ["totalAmountMdl"] = snapshot.Outcome?.Amount?.Amount ?? 0m,
            };
        }

        // Unreachable on the auto-generation path — kept as a defensive default so the
        // method never returns null.
        return new Dictionary<string, object?>();
    }

    /// <summary>
    /// Renders the supplied rows as a minimal-viable Office Open XML word-processing
    /// document. Layout:
    /// <list type="bullet">
    ///   <item>Heading paragraph: <paramref name="title"/>.</item>
    ///   <item>Body paragraphs — one per row, formatted as <c>{field}: {value}</c>.</item>
    ///   <item>Footer paragraph: <c>Generat automat la {clock.UtcNow}</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="title">Top heading text (e.g. "Decizia nr. D-2026-...").</param>
    /// <param name="snapshot">Dossier snapshot — used only for the body lines.</param>
    /// <param name="rows">Pre-built (label, value) rows to serialise.</param>
    /// <returns>Raw DOCX bytes (zip envelope) ready to be uploaded to storage.</returns>
    private byte[] BuildDocx(
        string title,
        DossierSnapshot snapshot,
        IReadOnlyList<(string Field, string Value)> rows)
    {
        // The Document entity is unused below — kept on the signature for symmetry with
        // the PDF renderers and to make extending the template (e.g. adding the dossier
        // number to a header) a one-line change.
        _ = snapshot;

        using var ms = new MemoryStream();
        // Leave the MemoryStream open after the package is disposed so the caller can
        // ToArray() it; OpenXML writes the central directory on Dispose.
        using (var package = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, autoSave: true))
        {
            var mainPart = package.AddMainDocumentPart();
            var body = new Body();

            // Heading.
            var headingRunProps = new RunProperties(
                new Bold(),
                new FontSize { Val = "28" });
            var headingRun = new Run(headingRunProps, new Text(title) { Space = SpaceProcessingModeValues.Preserve });
            body.AppendChild(new Paragraph(headingRun));

            // Body — one paragraph per (field, value) row.
            foreach (var (field, value) in rows)
            {
                var labelRun = new Run(
                    new RunProperties(new Bold()),
                    new Text($"{field}: ") { Space = SpaceProcessingModeValues.Preserve });
                var valueRun = new Run(new Text(value ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });
                body.AppendChild(new Paragraph(labelRun, valueRun));
            }

            // Footer.
            var footerText = string.Create(
                CultureInfo.InvariantCulture,
                $"Generat automat la {_clock.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            var footerRun = new Run(
                new RunProperties(new Italic()),
                new Text(footerText) { Space = SpaceProcessingModeValues.Preserve });
            body.AppendChild(new Paragraph(footerRun));

            mainPart.Document = new WordDocument(body);
            mainPart.Document.Save();
        }

        return ms.ToArray();
    }

    /// <summary>
    /// View-model wrapping the data necessary to render either the Fișa de calcul or
    /// the Decizia. Immutable; built once per generation call.
    /// </summary>
    /// <param name="DossierNumber">Internal dossier number (e.g. <c>D-2026-ABCD1234</c>).</param>
    /// <param name="ApplicantNationalId">IDNP/IDNO of the applicant.</param>
    /// <param name="ApplicantDisplayName">Display name of the applicant.</param>
    /// <param name="PassportCode">Stable service-passport code.</param>
    /// <param name="PassportName">Romanian display name of the service passport.</param>
    /// <param name="ApplicationReference">Public reference number of the application.</param>
    /// <param name="ApplicationSubmittedAtUtc">When the application was submitted (UTC).</param>
    /// <param name="Outcome">Decision-engine outcome (may be <see langword="null"/> on parse failure).</param>
    /// <param name="GeneratedAtUtc">Timestamp stamped on the document (UTC).</param>
    private sealed record DossierSnapshot(
        string DossierNumber,
        string ApplicantNationalId,
        string ApplicantDisplayName,
        string PassportCode,
        string PassportName,
        string ApplicationReference,
        DateTime? ApplicationSubmittedAtUtc,
        DecisionOutcome? Outcome,
        DateTime GeneratedAtUtc);
}
