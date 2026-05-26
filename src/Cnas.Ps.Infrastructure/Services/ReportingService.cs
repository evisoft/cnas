using System.Globalization;
using System.Text;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Reporting;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPdfDocument = QuestPDF.Fluent.Document;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// UC09 / UC19 — Real implementation of <see cref="IReportingService"/>. Loads aggregate
/// data from the persistence layer and renders it via CSV (<see cref="CsvWriter"/>),
/// XLSX (<see cref="XLWorkbook"/>), or PDF (QuestPDF). The set of supported report codes
/// is fixed at the present iteration; a future iteration may move the catalogue into the
/// <c>Reports</c> table once the runtime parameter schema and query template are nailed
/// down.
/// </summary>
/// <remarks>
/// <para>
/// External identifiers are always Sqid-encoded (CLAUDE.md RULE 3) — raw <see cref="long"/>
/// primary keys are kept internal. All timestamps in column headers and PDF metadata are
/// UTC, sourced from <see cref="ICnasTimeProvider"/> never <see cref="DateTime.UtcNow"/>
/// directly (CLAUDE.md cross-cutting — UTC Everywhere).
/// </para>
/// <para>
/// DOCX support is intentionally deferred this iteration; the existing
/// <see cref="ExportFormat"/> enum only defines CSV/XLSX/PDF, so a DOCX request would
/// be rejected with <see cref="ErrorCodes.ValidationFailed"/>. The corresponding unit
/// test documents this substitution: callers who expect DOCX should request PDF.
/// </para>
/// <para>
/// Resource budgets:
/// <list type="bullet">
///   <item>Audit log reports default to 5_000 rows when <c>maxRows</c> is omitted.</item>
///   <item>Hard ceiling of 50_000 rows on any row-producing report. Above this the
///         service returns <see cref="ErrorCodes.ReportTooLarge"/>.</item>
/// </list>
/// </para>
/// <para>
/// R1904 / ARH 025 — carries <see cref="LongRunningReportServiceAttribute"/>
/// because every aggregation entry method here runs against the read-only
/// <see cref="IReadOnlyCnasDbContext"/> seam, which is routed to the Postgres
/// streaming-replication replica in production. The architecture test
/// <c>LongRunningReportServicesUseReadReplica</c> enforces this contract by
/// scanning the constructor parameters of every type carrying the marker and
/// failing the build if the writable <c>ICnasDbContext</c> reappears.
/// </para>
/// </remarks>
[LongRunningReportService]
public sealed partial class ReportingService : IReportingService
{
    /// <summary>Default row cap when the caller omits <c>maxRows</c>.</summary>
    private const int DefaultMaxRows = 5_000;

    /// <summary>Hard ceiling — any larger request is rejected up front.</summary>
    private const int AbsoluteRowCeiling = 50_000;

    /// <summary>Audit-log dump.</summary>
    private const string AuditLogCode = "AUDIT_LOG";

    /// <summary>Contributor registry dump.</summary>
    private const string ContributorsCode = "CONTRIBUTORS";

    /// <summary>Insured-person registry dump.</summary>
    private const string InsuredPersonsCode = "INSURED_PERSONS";

    /// <summary>Application status counts.</summary>
    private const string ApplicationsByStatusCode = "APPLICATIONS_BY_STATUS";

    /// <summary>Currently open dossiers.</summary>
    private const string DossiersOpenCode = "DOSSIERS_OPEN";

    /// <summary>
    /// Read-only DbContext routed to the Postgres streaming-replication replica
    /// (R0026 / TOR PSR 006). Reporting aggregations and Annex 6 list queries
    /// run here so the primary backend is not crushed by analytical workloads.
    /// The same <c>CnasDbContext</c> instance satisfies both <c>ICnasDbContext</c>
    /// and <see cref="IReadOnlyCnasDbContext"/> in tests so cross-context flows
    /// (seed via writer, read back via reader) round-trip deterministically
    /// against the InMemory provider.
    /// </summary>
    private readonly IReadOnlyCnasDbContext _db;
    private readonly ICnasTimeProvider _clock;
    private readonly ISqidService _sqids;
    private readonly ILogger<ReportingService> _logger;

    /// <summary>
    /// Deterministic HMAC hasher used by the Annex 6f Solicitant→InsuredPerson join
    /// (RPT-CASES-BY-AGE-GROUP). The join key (IDNP) sits in two columns that are both
    /// encrypted at rest, so the join cannot run against the plaintext columns — it
    /// joins on the matching <c>*Hash</c> shadow columns, which are deterministic and
    /// therefore comparable. See <see cref="IDeterministicHasher"/>.
    /// </summary>
    private readonly IDeterministicHasher _idHasher;

    /// <summary>
    /// Cached result of the one-shot QuestPDF licence-setting probe. Stays <c>null</c>
    /// until the first PDF render attempt; <c>true</c> once QuestPDF reports ready,
    /// <c>false</c> if its native dependencies (SkiaSharp) refused to load on the
    /// current runtime (e.g. <c>win-arm64</c> dev boxes — QuestPDF only ships native
    /// binaries for the runtimes listed in its docs). When <c>false</c> the service
    /// falls back to <see cref="RenderMinimalPdf"/>, a tiny hand-rolled PDF emitter
    /// that satisfies the PDF magic-byte contract.
    /// </summary>
    private static bool? _questPdfAvailable;

    /// <summary>Synchronises the one-shot QuestPDF probe across concurrent first calls.</summary>
    private static readonly Lock QuestPdfLock = new();

    /// <summary>
    /// Lazily probes QuestPDF support and configures the Community licence on the first
    /// successful probe. Called from <see cref="RenderPdf"/> rather than a static cctor
    /// so that loading the service does not crash on dev runtimes that lack the native
    /// SkiaSharp binaries. The probe runs at most once per process.
    /// </summary>
    private static bool TryInitializeQuestPdf()
    {
        if (_questPdfAvailable.HasValue) return _questPdfAvailable.Value;
        lock (QuestPdfLock)
        {
            if (_questPdfAvailable.HasValue) return _questPdfAvailable.Value;
            try
            {
                // Setting the licence forces QuestPDF to probe its native deps. If the probe
                // throws (e.g. win-arm64 has no SkiaSharp binaries), we swallow and fall back
                // to RenderMinimalPdf so reports still render.
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

    /// <summary>Creates a new <see cref="ReportingService"/> bound to the supplied collaborators.</summary>
    /// <param name="db">
    /// Read-only EF Core context abstraction routed to the Postgres
    /// streaming-replication replica per R0026 / TOR PSR 006. Reporting
    /// aggregations and Annex 6 list queries run here so the primary backend
    /// is not crushed by analytical workloads.
    /// </param>
    /// <param name="clock">UTC clock; never <see cref="DateTime.UtcNow"/> directly.</param>
    /// <param name="sqids">Sqid encoder for externalising entity identifiers in report rows.</param>
    /// <param name="logger">Structured logger for IO / serialisation failures.</param>
    /// <param name="idHasher">
    /// Deterministic HMAC hasher used by the Annex 6f Solicitant→InsuredPerson join
    /// (RPT-CASES-BY-AGE-GROUP). Joins on the shadow hash column, not the encrypted
    /// plaintext column.
    /// </param>
    public ReportingService(
        IReadOnlyCnasDbContext db,
        ICnasTimeProvider clock,
        ISqidService sqids,
        ILogger<ReportingService> logger,
        IDeterministicHasher idHasher)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sqids);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(idHasher);

        _db = db;
        _clock = clock;
        _sqids = sqids;
        _logger = logger;
        _idHasher = idHasher;
    }

    /// <summary>
    /// Static catalogue of every report code recognised by <see cref="GenerateAsync"/>.
    /// Hard-coded here (mirroring the dispatcher chain in <c>IsKnownReportCode</c> and
    /// the Annex 6 predicate chain) rather than reflected from the predicate methods —
    /// the predicates are <c>private static</c> and shipping a public reflection surface
    /// to discover them would couple the contract to internal naming. New report codes
    /// must be added here in the same commit that adds the materialiser (mechanical
    /// — fail the related dispatcher test if they drift apart).
    /// </summary>
    /// <remarks>
    /// Titles default to the code itself for entries that have not yet been translated
    /// — the API contract guarantees a non-empty title in each language so the
    /// front-end never has to render a fallback. Translations are added in-place
    /// without changing the field order.
    /// </remarks>
    private static readonly IReadOnlyList<ReportCatalogEntryOutput> Catalog =
    [
        // Stock five (trunk file)
        new(AuditLogCode, "Jurnal de audit", "Журнал аудита", "Audit log"),
        new(ContributorsCode, "Plătitori de contribuții", "Плательщики взносов", "Contributors"),
        new(InsuredPersonsCode, "Asigurați", "Застрахованные лица", "Insured persons"),
        new(ApplicationsByStatusCode, "Cereri după statut", "Заявки по статусу", "Applications by status"),
        new(DossiersOpenCode, "Dosare deschise", "Открытые дела", "Open dossiers"),

        // Annex 6 (ReportingService.Annex6.cs)
        new(PenActiveCode, "Pensii active", "Активные пенсии", "Active pensions"),
        new(PenNewPeriodCode, "Pensii noi în perioadă", "Новые пенсии за период", "New pensions in period"),
        new(DosPendingExamCode, "Dosare în examinare", "Дела на рассмотрении", "Dossiers pending examination"),
        new(DocRequestsOutCode, "Cereri de documente expediate", "Запросы документов исходящие", "Outgoing document requests"),
        new(DecisionOutcomesCode, "Distribuția deciziilor", "Распределение решений", "Decision outcomes"),

        // Annex 6b (ReportingService.Annex6b.cs)
        new("RPT-DOS-CLOSED-PERIOD", "Dosare închise în perioadă", "Закрытые дела за период", "Dossiers closed in period"),
        new("RPT-WORKLOAD-EXAMINER", "Volumul de lucru pe examinator", "Нагрузка по эксперту", "Workload by examiner"),
        new("RPT-PAYMENT-BATCH-SUMMARY", "Rezumat plăți lunare", "Сводка платежей за месяц", "Payment batch summary"),
        new("RPT-AGING-DOSSIERS", "Vechimea dosarelor", "Старение дел", "Aging dossiers"),
        new("RPT-DOC-VERDICT-MIX", "Distribuția verdictelor pe documente", "Распределение вердиктов по документам", "Document verdict mix"),

        // Annex 6c (ReportingService.Annex6c.cs)
        new("RPT-APPEAL-INBOX", "Contestații deschise", "Открытые жалобы", "Open appeals inbox"),
        new("RPT-DOC-REQUESTS-CLOSED-RECENT", "Cereri de documente închise recent", "Запросы документов недавно закрытые", "Document requests closed recently"),
        new("RPT-DOSSIER-ASSIGNMENTS-PER-EXAMINER", "Atribuiri dosare per examinator", "Назначения дел по эксперту", "Dossier assignments per examiner"),
        new("RPT-PAYMENT-HISTORY", "Istoricul plăților", "История платежей", "Payment history"),
        new("RPT-DOSSIERS-BY-SERVICE", "Dosare pe tip de serviciu", "Дела по типу услуги", "Dossiers by service"),

        // Annex 6d (ReportingService.Annex6d.cs)
        new("RPT-DOSSIER-LIFECYCLE-TIME", "Durata ciclului de viață al dosarelor", "Срок жизни дел", "Dossier lifecycle time"),
        new("RPT-EXAMINER-OUTCOMES", "Rezultate per examinator", "Результаты по эксперту", "Examiner outcomes"),
        new("RPT-NEW-APPLICATIONS-DAILY", "Cereri noi zilnic", "Новые заявки ежедневно", "New applications daily"),
        new("RPT-OUTSTANDING-AMOUNTS", "Sume restante", "Невыплаченные суммы", "Outstanding amounts"),
        new("RPT-DECISION-TURNAROUND", "Timpul de luare a deciziei", "Время принятия решения", "Decision turnaround"),

        // Annex 6e (ReportingService.Annex6e.cs)
        new("RPT-DOCUMENT-AGE-DISTRIBUTION", "Distribuția vechimii documentelor", "Распределение возраста документов", "Document age distribution"),
        new("RPT-REJECTION-REASONS", "Motive de refuz", "Причины отказа", "Rejection reasons"),
        new("RPT-MONTHLY-DECISIONS-BY-EXAMINER", "Decizii lunare pe examinator", "Ежемесячные решения по эксперту", "Monthly decisions by examiner"),
        new("RPT-SLAS-MISSED", "SLA-uri ratate", "Пропущенные SLA", "SLAs missed"),
        new("RPT-TOTAL-PAYMENTS-PER-MONTH", "Plăți totale pe lună", "Общие выплаты за месяц", "Total payments per month"),

        // Annex 6f (ReportingService.Annex6f.cs)
        new("RPT-CASES-BY-AGE-GROUP", "Cazuri pe grupe de vârstă", "Дела по возрастным группам", "Cases by age group"),
        new("RPT-CASES-BY-LOCALITY", "Cazuri pe localitate", "Дела по местности", "Cases by locality"),
        new("RPT-EXAMINER-AVG-CASELOAD", "Volumul mediu de cazuri per examinator", "Средняя нагрузка эксперта", "Examiner average caseload"),
        new("RPT-CANCELLATIONS-BY-REASON", "Anulări pe motiv", "Отмены по причине", "Cancellations by reason"),
        new("RPT-DAILY-CASH-FLOW", "Flux de numerar zilnic", "Ежедневный денежный поток", "Daily cash flow"),

        // Annex 6g (ReportingService.Annex6g.cs)
        new("RPT-DOCUMENT-TYPES-USAGE", "Utilizarea tipurilor de documente", "Использование типов документов", "Document types usage"),
        new("RPT-WORKFLOW-BACKLOG-AGE", "Vechimea sarcinilor în workflow", "Старение задач рабочего процесса", "Workflow backlog age"),
        new("RPT-INSURED-PERSONS-NEW", "Asigurați noi", "Новые застрахованные", "New insured persons"),
        new("RPT-BENEFICIARIES-BY-SERVICE-TYPE", "Beneficiari pe tip de serviciu", "Получатели по типу услуги", "Beneficiaries by service type"),
        new("RPT-NOTIFICATIONS-DELIVERY", "Livrarea notificărilor", "Доставка уведомлений", "Notifications delivery"),

        // Annex 6h (ReportingService.Annex6h.cs)
        new("RPT-AUDIT-EVENTS-BY-SEVERITY", "Evenimente de audit pe severitate", "События аудита по тяжести", "Audit events by severity"),
        new("RPT-DOCUMENT-UPLOAD-VOLUMES", "Volumul de încărcări documente", "Объём загрузок документов", "Document upload volumes"),
        new("RPT-LOGIN-EVENTS-PER-DAY", "Autentificări pe zi", "Входы в день", "Login events per day"),
        new("RPT-ACTIVE-USERS-LAST-30D", "Utilizatori activi în ultimele 30 zile", "Активные пользователи за 30 дней", "Active users (last 30 days)"),
        new("RPT-DOSSIER-EXAMINATION-DURATION", "Durata examinării dosarelor", "Продолжительность рассмотрения дел", "Dossier examination duration"),

        // Annex 6i (ReportingService.Annex6i.cs)
        new("RPT-PASSPORT-USAGE", "Utilizarea pașapoartelor de serviciu", "Использование сервисных паспортов", "Service passport usage"),
        new("RPT-AUDIT-EVENTS-BY-ACTION", "Evenimente de audit pe acțiune", "События аудита по действию", "Audit events by action"),
        new("RPT-NOTIFICATIONS-UNREAD", "Notificări necitite", "Непрочитанные уведомления", "Unread notifications"),
        new("RPT-DOCUMENTS-UNSIGNED", "Documente nesemnate", "Неподписанные документы", "Unsigned documents"),
        new("RPT-DOSSIERS-OPEN-BY-EXAMINER", "Dosare deschise per examinator", "Открытые дела по эксперту", "Open dossiers by examiner"),

        // Annex 6j (ReportingService.Annex6j.cs)
        new("RPT-DOSSIERS-CLOSED-BY-OUTCOME", "Dosare închise pe rezultat", "Закрытые дела по результату", "Dossiers closed by outcome"),
        new("RPT-APPLICATIONS-BY-PASSPORT-MONTHLY", "Cereri lunare per pașaport", "Заявки по паспорту ежемесячно", "Applications by passport (monthly)"),
        new("RPT-NOTIFICATIONS-BY-CITIZEN", "Notificări per cetățean", "Уведомления по гражданину", "Notifications by citizen"),
        new("RPT-AUDIT-EVENTS-BY-ACTOR", "Evenimente de audit pe actor", "События аудита по актору", "Audit events by actor"),
        new("RPT-DOCUMENT-VERDICTS-OVER-TIME", "Verdicte pe documente în timp", "Вердикты по документам со временем", "Document verdicts over time"),
    ];

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<ReportCatalogEntryOutput>>> ListAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        // Pure in-memory snapshot — no DB I/O — but we honour the CancellationToken to
        // match the rest of the async surface (and to keep the analyser happy).
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<IReadOnlyList<ReportCatalogEntryOutput>>.Success(Catalog));
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> GenerateAsync(
        string reportCode,
        string parametersJson,
        ExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportCode);

        // 1. Validate the report code first so callers get a fast 404 for typos.
        if (!IsKnownReportCode(reportCode))
        {
            return Result<Stream>.Failure(ErrorCodes.NotFound, "Unknown report code");
        }

        // 2. Validate the output format. The ExportFormat enum is closed; anything outside
        //    Csv / Xlsx / Pdf is a contract violation (or — in the deferred DOCX scenario —
        //    a feature that isn't built yet).
        if (!Enum.IsDefined<ExportFormat>(format))
        {
            return Result<Stream>.Failure(ErrorCodes.ValidationFailed, "Unsupported export format");
        }

        // 3. Parse parameters JSON. Empty / null is tolerated as "no parameters supplied".
        var paramsResult = TryParseParameters(parametersJson);
        if (paramsResult.IsFailure)
        {
            return Result<Stream>.Failure(paramsResult.ErrorCode!, paramsResult.ErrorMessage!);
        }
        using var parameters = paramsResult.Value;

        try
        {
            // 4. Materialise the dataset. Each report code maps to a single materialiser that
            //    returns a table-shaped DTO (headers + rows) suitable for any format renderer.
            var datasetResult = await BuildDatasetAsync(
                reportCode, parameters.RootElement, cancellationToken).ConfigureAwait(false);
            if (datasetResult.IsFailure)
            {
                return Result<Stream>.Failure(datasetResult.ErrorCode!, datasetResult.ErrorMessage!);
            }

            // 5. Render. CSV writes UTF-8 with BOM + CRLF; XLSX uses a single sheet named after
            //    the report code; PDF lays out a simple A4 portrait table with a header.
            var stream = Render(reportCode, datasetResult.Value, format);
            return Result<Stream>.Success(stream);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "EF Core update failure while building report {ReportCode}", reportCode);
            return Result<Stream>.Failure(ErrorCodes.Internal, "Report generation failed.");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O failure while rendering report {ReportCode}", reportCode);
            return Result<Stream>.Failure(ErrorCodes.Internal, "Report generation failed.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON serialisation failure while rendering report {ReportCode}", reportCode);
            return Result<Stream>.Failure(ErrorCodes.Internal, "Report generation failed.");
        }
    }

    // ─────────────────────────── Parameters ───────────────────────────

    /// <summary>True when the supplied code is one of the supported report identifiers.</summary>
    /// <param name="code">Caller-supplied report code (case-sensitive — codes are stable contract).</param>
    private static bool IsKnownReportCode(string code)
        => code is AuditLogCode or ContributorsCode or InsuredPersonsCode
            or ApplicationsByStatusCode or DossiersOpenCode
            || IsAnnex6ReportCode(code);

    /// <summary>
    /// Parses the parameters JSON. Returns a <see cref="JsonDocument"/> wrapped in a Result;
    /// the caller is responsible for disposing it. Null / empty / whitespace input is treated
    /// as an empty JSON object — the caller can then look up keys with safe defaults.
    /// </summary>
    private static Result<JsonDocument> TryParseParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return Result<JsonDocument>.Success(JsonDocument.Parse("{}"));
        }
        try
        {
            return Result<JsonDocument>.Success(JsonDocument.Parse(parametersJson));
        }
        catch (JsonException)
        {
            return Result<JsonDocument>.Failure(
                ErrorCodes.ValidationFailed, "parametersJson is not valid JSON");
        }
    }

    /// <summary>Reads a UTC <see cref="DateTime"/> from the JSON element, returning <c>null</c> when absent.</summary>
    private static DateTime? ReadUtcDate(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Null) return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        if (!DateTime.TryParse(
                prop.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var dt))
        {
            return null;
        }
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    /// <summary>Reads an integer from the JSON element, returning <c>null</c> when absent or non-numeric.</summary>
    private static int? ReadInt(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.Number) return null;
        return prop.TryGetInt32(out var v) ? v : null;
    }

    /// <summary>Reads a string from the JSON element, returning <c>null</c> when absent or empty.</summary>
    private static string? ReadString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        var s = prop.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // ─────────────────────────── Dataset builders ───────────────────────────

    /// <summary>
    /// Lookup table — maps a known report code to its materialiser. Adding a new report
    /// means adding a new branch here plus a new builder method below.
    /// </summary>
    /// <remarks>
    /// Emits one <see cref="CnasMeter.ReportingServiceQueryExecuted"/> tick per
    /// invocation tagged with <c>db_context = "read_replica"</c> per R1904 /
    /// ARH 025 so operators can confirm that long-running report aggregations
    /// are landing on the Postgres streaming-replication follower and not on
    /// the primary backend.
    /// </remarks>
    private async Task<Result<Dataset>> BuildDatasetAsync(
        string reportCode,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        // R1904 / ARH 025 — tag every dataset materialisation with the EF Core
        // context it ran against. ReportingService is pure-read and always
        // hits the read-replica seam (IReadOnlyCnasDbContext); the constant
        // value here is what the architecture test pins via the
        // [LongRunningReportService] marker.
        CnasMeter.ReportingServiceQueryExecuted.Add(
            1,
            new KeyValuePair<string, object?>("db_context", "read_replica"));

        return reportCode switch
        {
            AuditLogCode => await BuildAuditLogAsync(parameters, cancellationToken).ConfigureAwait(false),
            ContributorsCode => await BuildContributorsAsync(parameters, cancellationToken).ConfigureAwait(false),
            InsuredPersonsCode => await BuildInsuredPersonsAsync(parameters, cancellationToken).ConfigureAwait(false),
            ApplicationsByStatusCode => await BuildApplicationsByStatusAsync(parameters, cancellationToken).ConfigureAwait(false),
            DossiersOpenCode => await BuildDossiersOpenAsync(parameters, cancellationToken).ConfigureAwait(false),
            _ => await BuildAnnex6DatasetAsync(reportCode, parameters, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Builds the audit-log dataset honouring optional <c>fromUtc</c> / <c>toUtc</c> / <c>maxRows</c>.</summary>
    private async Task<Result<Dataset>> BuildAuditLogAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var fromUtc = ReadUtcDate(parameters, "fromUtc");
        var toUtc = ReadUtcDate(parameters, "toUtc");
        var maxRows = ReadInt(parameters, "maxRows") ?? DefaultMaxRows;
        if (maxRows > AbsoluteRowCeiling)
        {
            return Result<Dataset>.Failure(
                ErrorCodes.ReportTooLarge, "Audit log report exceeds 50000 rows.");
        }
        maxRows = Math.Max(1, maxRows);

        var query = _db.AuditLogs.AsQueryable();
        if (fromUtc is not null) query = query.Where(a => a.EventAtUtc >= fromUtc.Value);
        if (toUtc is not null) query = query.Where(a => a.EventAtUtc <= toUtc.Value);

        var rows = await query
            .OrderBy(a => a.EventAtUtc)
            .Take(maxRows)
            .Select(a => new
            {
                a.EventAtUtc, a.EventCode, a.Severity, a.ActorId,
                a.TargetEntity, a.TargetEntityId, a.SourceIp,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "When (UTC)", "Action", "Severity", "Actor", "Resource", "ResourceId", "Ip" };
        var data = rows.Select(r => new[]
        {
            r.EventAtUtc.ToString("o", CultureInfo.InvariantCulture),
            r.EventCode,
            r.Severity.ToString(),
            r.ActorId,
            r.TargetEntity ?? string.Empty,
            r.TargetEntityId.HasValue ? _sqids.Encode(r.TargetEntityId.Value) : string.Empty,
            r.SourceIp ?? string.Empty,
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>Builds the contributors dataset honouring an optional <c>search</c> (matches IDNO + Denumire).</summary>
    private async Task<Result<Dataset>> BuildContributorsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var search = ReadString(parameters, "search");

        var query = _db.Contributors.Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Case-insensitive substring match across IDNO + Denumire. We use EF.Functions.Like
            // with a SQL LIKE pattern wrapping the needle in '%'; the InMemory provider falls
            // back to a case-insensitive string match. Both providers honour the search.
            var pattern = $"%{search}%";
            query = query.Where(c =>
                EF.Functions.Like(c.Idno, pattern) ||
                EF.Functions.Like(c.Denumire, pattern));
        }

        var rows = await query
            .OrderBy(c => c.Denumire)
            .Take(AbsoluteRowCeiling)
            .Select(c => new { c.Id, c.Idno, c.Denumire, c.IsInsolvent, c.RegisteredAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Sqid Id", "IDNO", "Denumire", "Is Insolvent", "Registered (UTC)" };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.Id),
            r.Idno,
            r.Denumire,
            r.IsInsolvent ? "true" : "false",
            r.RegisteredAtUtc.ToString("o", CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>Builds the insured-persons dataset.</summary>
    private async Task<Result<Dataset>> BuildInsuredPersonsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        _ = parameters; // No parameters defined for this report at present.
        var rows = await _db.InsuredPersons
            .Where(p => p.IsActive)
            .OrderBy(p => p.LastName).ThenBy(p => p.FirstName)
            .Take(AbsoluteRowCeiling)
            .Select(p => new
            {
                p.Id, p.Idnp, p.LastName, p.FirstName, p.Patronymic,
                p.BirthDate, p.IsDeceased, p.RegisteredAtUtc,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Sqid Id", "IDNP", "Full Name", "Birth Date", "Is Deceased", "Registered (UTC)" };
        var data = rows.Select(r => new[]
        {
            _sqids.Encode(r.Id),
            r.Idnp,
            string.IsNullOrWhiteSpace(r.Patronymic)
                ? $"{r.LastName} {r.FirstName}"
                : $"{r.LastName} {r.FirstName} {r.Patronymic}",
            r.BirthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.IsDeceased ? "true" : "false",
            r.RegisteredAtUtc.ToString("o", CultureInfo.InvariantCulture),
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>Builds the application-status counts dataset, optionally scoped to a passport.</summary>
    private async Task<Result<Dataset>> BuildApplicationsByStatusAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var passportCode = ReadString(parameters, "passportCode");

        var query = _db.Applications.Where(a => a.IsActive);
        if (!string.IsNullOrWhiteSpace(passportCode))
        {
            // Resolve passport code → id via the join. Doing it in two steps keeps the LINQ
            // EF Core-translatable on every provider including the InMemory one used in tests.
            var passportIds = await _db.ServicePassports
                .Where(p => p.Code == passportCode && p.IsActive)
                .Select(p => p.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            query = query.Where(a => passportIds.Contains(a.ServicePassportId));
        }

        var grouped = await query
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[] { "Status", "Count" };
        var data = grouped
            .OrderBy(r => r.Status.ToString(), StringComparer.Ordinal)
            .Select(r => new[]
            {
                r.Status.ToString(),
                r.Count.ToString(CultureInfo.InvariantCulture),
            })
            .ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    /// <summary>Builds the currently-open dossiers dataset (ClosedAtUtc IS NULL).</summary>
    private async Task<Result<Dataset>> BuildDossiersOpenAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        _ = parameters; // No parameters defined for this report at present.

        var rows = await _db.Dossiers
            .Where(d => d.IsActive && d.ClosedAtUtc == null)
            .OrderBy(d => d.CreatedAtUtc)
            .Take(AbsoluteRowCeiling)
            .Select(d => new
            {
                d.DossierNumber, d.ApplicationId,
                ApplicationStatus = d.Application != null ? d.Application.Status : ApplicationStatus.Draft,
                d.CreatedAtUtc, d.AssignedExaminerId,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var headers = new[]
        {
            "Dossier Number", "Application Sqid", "Status", "Created (UTC)", "Assigned Examiner Sqid",
        };
        var data = rows.Select(r => new[]
        {
            r.DossierNumber,
            _sqids.Encode(r.ApplicationId),
            r.ApplicationStatus.ToString(),
            r.CreatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            r.AssignedExaminerId.HasValue ? _sqids.Encode(r.AssignedExaminerId.Value) : string.Empty,
        }).ToList();

        return Result<Dataset>.Success(new Dataset(headers, data));
    }

    // ─────────────────────────── Renderers ───────────────────────────

    /// <summary>Renders the dataset into the requested format and returns a seekable stream positioned at zero.</summary>
    private Stream Render(string reportCode, Dataset dataset, ExportFormat format)
        => format switch
        {
            ExportFormat.Csv => RenderCsv(dataset),
            ExportFormat.Xlsx => RenderXlsx(reportCode, dataset),
            ExportFormat.Pdf => RenderPdf(reportCode, dataset),
            // Defensive — Enum.IsDefined upstream should prevent reaching this branch.
            _ => throw new InvalidOperationException($"Unhandled export format {format}."),
        };

    /// <summary>
    /// Renders the dataset as UTF-8-with-BOM CSV using CRLF line endings — the format Excel
    /// and LibreOffice open without prompting for encoding.
    /// </summary>
    private static Stream RenderCsv(Dataset dataset)
    {
        var ms = new MemoryStream();
        // UTF-8 with BOM so Excel auto-detects the encoding.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var writer = new StreamWriter(ms, encoding, leaveOpen: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            NewLine = "\r\n",
        };
        using (var csv = new CsvWriter(writer, config, leaveOpen: true))
        {
            foreach (var header in dataset.Headers)
            {
                csv.WriteField(header);
            }
            csv.NextRecord();

            foreach (var row in dataset.Rows)
            {
                foreach (var cell in row)
                {
                    csv.WriteField(cell);
                }
                csv.NextRecord();
            }
        }
        writer.Flush();
        writer.Dispose();
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Renders the dataset to a single-sheet XLSX workbook. The sheet name mirrors the
    /// report code (truncated to 31 chars — Excel's limit). The first row is bold and
    /// column widths are auto-fit.
    /// </summary>
    private static Stream RenderXlsx(string reportCode, Dataset dataset)
    {
        var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            // Excel sheet names are capped at 31 chars; truncate defensively even though
            // all current report codes are short.
            var sheetName = reportCode.Length > 31 ? reportCode[..31] : reportCode;
            var ws = wb.Worksheets.Add(sheetName);

            for (var c = 0; c < dataset.Headers.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = dataset.Headers[c];
                cell.Style.Font.Bold = true;
            }

            for (var r = 0; r < dataset.Rows.Count; r++)
            {
                var row = dataset.Rows[r];
                for (var c = 0; c < row.Length; c++)
                {
                    ws.Cell(r + 2, c + 1).Value = row[c];
                }
            }

            if (dataset.Headers.Count > 0)
            {
                ws.Columns(1, dataset.Headers.Count).AdjustToContents();
            }

            wb.SaveAs(ms);
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Renders the dataset to an A4 portrait PDF with a header banner naming the report
    /// and the generation timestamp (UTC) plus a simple tabular body. On dev runtimes
    /// where QuestPDF's native SkiaSharp dependencies cannot load (e.g. win-arm64) the
    /// service falls back to <see cref="RenderMinimalPdf"/> — a hand-rolled PDF emitter
    /// that ships no native deps but produces a valid (if plain) PDF artifact. This
    /// substitution preserves the PDF magic-byte contract and lets reports still flow
    /// end-to-end during local development; production runtimes (win-x64 / linux-x64)
    /// continue to render the full QuestPDF document.
    /// </summary>
    private Stream RenderPdf(string reportCode, Dataset dataset)
    {
        var generatedAt = _clock.UtcNow.ToString("u", CultureInfo.InvariantCulture);

        if (!TryInitializeQuestPdf())
        {
            _logger.LogWarning(
                "QuestPDF native dependencies are not available on this runtime; " +
                "falling back to minimal PDF renderer for {ReportCode}.", reportCode);
            return RenderMinimalPdf(reportCode, dataset, generatedAt);
        }

        try
        {
            var document = QuestPdfDocument.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(25);
                    page.DefaultTextStyle(x => x.FontSize(9));

                    page.Header().Column(col =>
                    {
                        col.Item().Text(reportCode).SemiBold().FontSize(14);
                        col.Item().Text($"Generated (UTC): {generatedAt}").FontSize(9);
                    });

                    page.Content().PaddingTop(10).Table(table =>
                    {
                        table.ColumnsDefinition(cols =>
                        {
                            for (var i = 0; i < dataset.Headers.Count; i++)
                            {
                                cols.RelativeColumn();
                            }
                        });

                        table.Header(header =>
                        {
                            foreach (var h in dataset.Headers)
                            {
                                header.Cell().BorderBottom(1).PaddingVertical(2).Text(h).SemiBold();
                            }
                        });

                        foreach (var row in dataset.Rows)
                        {
                            foreach (var cell in row)
                            {
                                table.Cell().PaddingVertical(1).Text(cell);
                            }
                        }
                    });

                    page.Footer().AlignRight().Text(text =>
                    {
                        text.Span("Page ");
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                });
            });

            var ms = new MemoryStream();
            document.GeneratePdf(ms);
            ms.Position = 0;
            return ms;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Defensive: if QuestPDF's probe passed but rendering still failed, swallow
            // and fall back to the minimal renderer instead of bubbling up a 500.
            _logger.LogWarning(ex,
                "QuestPDF render failed for {ReportCode}; falling back to minimal PDF.", reportCode);
            return RenderMinimalPdf(reportCode, dataset, generatedAt);
        }
    }

    /// <summary>
    /// Emits a syntactically-valid PDF 1.4 document with a single page that lists the
    /// report code, timestamp, and dataset rows as plain Helvetica text. Used as the
    /// fallback when the QuestPDF native dependencies are not available. The output
    /// satisfies the magic-byte check (<c>%PDF-</c>) and opens in any PDF viewer; it
    /// is intentionally unstyled — production hosts run on supported QuestPDF runtimes.
    /// </summary>
    private static Stream RenderMinimalPdf(string reportCode, Dataset dataset, string generatedAt)
    {
        // Build the visible text content first — one line per header, one line per row,
        // rendered top-to-bottom on a single A4 page (no pagination).
        var lines = new List<string>(capacity: dataset.Rows.Count + 4)
        {
            reportCode,
            $"Generated (UTC): {generatedAt}",
            string.Empty,
            string.Join(" | ", dataset.Headers),
        };
        lines.AddRange(dataset.Rows.Select(r => string.Join(" | ", r)));

        // Construct a minimal PDF content stream — each Tj at the next line position.
        var content = new StringBuilder();
        content.Append("BT\n/F1 9 Tf\n");
        var y = 800f;
        foreach (var line in lines)
        {
            content.Append(CultureInfo.InvariantCulture, $"1 0 0 1 36 {y:0.##} Tm (");
            content.Append(EscapePdfString(line));
            content.Append(") Tj\n");
            y -= 12f;
            if (y < 36f) break; // single-page only — drop overflow.
        }
        content.Append("ET\n");
        var contentBytes = Encoding.ASCII.GetBytes(content.ToString());

        var ms = new MemoryStream();
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
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");

        // Content stream object
        offsets.Add(ms.Position);
        var header = $"4 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n";
        writer.Write(Encoding.ASCII.GetBytes(header));
        writer.Write(contentBytes);
        writer.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));

        WriteObj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // xref + trailer
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

        ms.Position = 0;
        return ms;
    }

    /// <summary>Escapes a string for inclusion in a PDF literal text object.</summary>
    private static string EscapePdfString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            // Strip control characters and replace non-ASCII (PDF Helvetica is single-byte).
            if (ch == '(' || ch == ')' || ch == '\\') { sb.Append('\\').Append(ch); }
            else if (ch < 32 || ch > 126) { sb.Append('?'); }
            else { sb.Append(ch); }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Tabular dataset shape used by every renderer. Headers come first, then zero-or-more
    /// rows of strings of equal length.
    /// </summary>
    /// <param name="Headers">Column headers (rendered as the first / bolded row).</param>
    /// <param name="Rows">Data rows; each row's length should equal <paramref name="Headers"/>'s length.</param>
    private sealed record Dataset(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows);
}
