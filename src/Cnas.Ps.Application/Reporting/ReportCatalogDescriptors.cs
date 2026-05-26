using System.Collections.Generic;
using Cnas.Ps.Core.Common;

namespace Cnas.Ps.Application.Reporting;

/// <summary>
/// R1900-R1905 / TOR §13 Annex 6 — static in-code descriptor table covering
/// the 55 implemented Annex 6 reports plus the 5 stock reports. Authoritative
/// source for the seeded <c>cnas.Reports</c> rows: when the
/// <c>IReportCatalogSeedService</c> runs it inserts or upserts one row per
/// descriptor so the catalog endpoint and the dashboard search picker always
/// reflect the implemented report set.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why 55 + 5.</b> The dispatcher in
/// <c>ReportingService.Annex6*.cs</c> recognises 5 codes per Annex 6 partial
/// (Annex 6 + 6b…6j → 10 partials × 5 = 50) plus the 5 stock codes
/// (<c>AUDIT_LOG</c>, <c>CONTRIBUTORS</c>, <c>INSURED_PERSONS</c>,
/// <c>APPLICATIONS_BY_STATUS</c>, <c>DOSSIERS_OPEN</c>) — total 55. Each
/// descriptor in this table maps 1:1 to a constant in those files; adding
/// a new dispatcher branch requires adding a descriptor here so the catalog
/// stays in lock-step with the materialiser.
/// </para>
/// <para>
/// <b>Frequency &amp; schedule.</b> The cadence column is informational —
/// the actual cron lives in the per-report Quartz job registration. The
/// table records the documented R1905 cadence so the UI can render it
/// without ranging Quartz.
/// </para>
/// </remarks>
public static class ReportCatalogDescriptors
{
    /// <summary>
    /// Returns the full descriptor table (frozen for ordinal-comparison
    /// stability). The list is deterministic — same instances on every call.
    /// </summary>
    public static IReadOnlyList<ReportCatalogDescriptor> All { get; } = Build();

    /// <summary>
    /// JSON snippets reused by multiple descriptors. Defined once so a wording
    /// change propagates without descriptor-by-descriptor edits.
    /// </summary>
    private const string FromToWindowParams =
        "{\"type\":\"object\",\"required\":[\"fromUtc\",\"toUtc\"]," +
        "\"properties\":{\"fromUtc\":{\"type\":\"string\",\"format\":\"date-time\"}," +
        "\"toUtc\":{\"type\":\"string\",\"format\":\"date-time\"}}}";

    private const string AsOfUtcParams =
        "{\"type\":\"object\",\"required\":[\"asOfUtc\"]," +
        "\"properties\":{\"asOfUtc\":{\"type\":\"string\",\"format\":\"date-time\"}}}";

    private const string MonthUtcParams =
        "{\"type\":\"object\",\"required\":[\"monthUtc\"]," +
        "\"properties\":{\"monthUtc\":{\"type\":\"string\",\"format\":\"date-time\"}}}";

    private const string NDaysParams =
        "{\"type\":\"object\",\"required\":[\"nDays\"]," +
        "\"properties\":{\"nDays\":{\"type\":\"integer\",\"minimum\":1}}}";

    private const string EmptyParams = "{}";

    private const string CommonFormats = "[\"csv\",\"xlsx\",\"pdf\"]";

    /// <summary>Builds the descriptor list.</summary>
    private static IReadOnlyList<ReportCatalogDescriptor> Build()
    {
        var list = new List<ReportCatalogDescriptor>(60)
        {
            // ── Stock five (registered in ReportingService.cs). ──
            new("AUDIT_LOG", "Jurnal audit",
                "Eveniment-cu-eveniment vizualizare a jurnalului de audit.",
                "auditor", "OnDemand", FromToWindowParams,
                "[\"EventCode\",\"Severity\",\"ActorId\",\"TargetEntity\",\"CreatedAtUtc\"]",
                RoleCodes.TechAdmin, "OnDemand", CommonFormats, "AuditSecurity", "csv"),

            new("CONTRIBUTORS", "Plătitori (Annex 1)",
                "Inventar al plătitorilor înregistrați și statutul lor.",
                "statistician", "Monthly", EmptyParams,
                "[\"ContributorCode\",\"DisplayName\",\"Status\"]",
                RoleCodes.Admin, "0 0 6 1 * ?", CommonFormats, "PayerRevenues", "xlsx"),

            new("INSURED_PERSONS", "Persoane asigurate (Annex 2)",
                "Inventar al persoanelor asigurate (Annex 2 §8.2.1).",
                "statistician", "Monthly", EmptyParams,
                "[\"InsuredPersonCode\",\"DisplayName\",\"Status\"]",
                RoleCodes.Admin, "0 0 6 1 * ?", CommonFormats, "Contributions", "xlsx"),

            new("APPLICATIONS_BY_STATUS", "Cereri pe status",
                "Distribuția cererilor pe status la momentul curent.",
                RoleCodes.Decider, "Weekly", EmptyParams,
                "[\"Status\",\"Count\"]",
                RoleCodes.Decider, "0 0 6 ? * MON", CommonFormats, "Statistical", "csv"),

            new("DOSSIERS_OPEN", "Dosare deschise",
                "Lista dosarelor în lucru ne-închise.",
                RoleCodes.Decider, "Daily", EmptyParams,
                "[\"DossierSqid\",\"OpenedUtc\",\"Status\"]",
                RoleCodes.Decider, "0 0 5 * * ?", CommonFormats, "DecisionsIssued", "csv"),

            // ── Annex 6 (5). ──
            new("RPT-PEN-ACTIVE", "Beneficiari pensii active",
                "Lista beneficiarilor cu pensie activă la data indicată.",
                RoleCodes.Decider, "OnDemand", AsOfUtcParams,
                "[\"DossierSqid\",\"BeneficiaryIdnp\",\"FullName\",\"ServiceCode\",\"MonthlyAmount\",\"GrantedFromUtc\"]",
                RoleCodes.Decider, "OnDemand", CommonFormats, "PaymentsProcessed", "xlsx"),

            new("RPT-PEN-NEW-PERIOD", "Pensii noi pe perioadă",
                "Pensii noi acordate în fereastra [fromUtc, toUtc).",
                "statistician", "Monthly", FromToWindowParams,
                "[\"DossierSqid\",\"BeneficiaryIdnp\",\"FullName\",\"ServiceCode\",\"DecisionUtc\",\"MonthlyAmount\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "DecisionsIssued", "xlsx"),

            new("RPT-DOS-PENDING-EXAM", "Dosare în examinare > N zile",
                "Dosare deschise în examinare a căror vechime depășește N zile.",
                "supervisor", "Daily", NDaysParams,
                "[\"DossierSqid\",\"BeneficiaryIdnp\",\"AssignedExaminer\",\"ReceivedUtc\",\"DaysOpen\"]",
                RoleCodes.Decider, "0 0 7 * * ?", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-DOC-REQUESTS-OUT", "Cereri documente nerezolvate",
                "Cereri externe de documente create dar nerezolvate.",
                "examiner", "Weekly", FromToWindowParams,
                "[\"RequestSqid\",\"DossierSqid\",\"TargetRegistry\",\"SentUtc\",\"AgeDays\"]",
                RoleCodes.Decider, "0 0 7 ? * MON", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-DECISION-OUTCOMES", "Distribuție rezultate decizii (lunar)",
                "Aprobate vs. respinse, grupate pe cod de serviciu, lunar.",
                "supervisor", "Monthly", MonthUtcParams,
                "[\"ServiceCode\",\"Outcome\",\"Count\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "DecisionsIssued", "xlsx"),

            // ── Annex 6b (5). ──
            new("RPT-DOS-CLOSED-PERIOD", "Dosare închise pe perioadă",
                "Dosare cu data de închidere în fereastra dată.",
                "statistician", "Monthly", FromToWindowParams,
                "[\"DossierSqid\",\"ClosedUtc\",\"Outcome\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "Statistical", "csv"),

            new("RPT-WORKLOAD-EXAMINER", "Volum de muncă pe examinator",
                "Numărul de dosare alocate fiecărui examinator activ.",
                "supervisor", "Weekly", EmptyParams,
                "[\"ExaminerSqid\",\"OpenCount\",\"ClosedCount\"]",
                RoleCodes.Admin, "0 0 7 ? * MON", CommonFormats, "PerformanceKpi", "xlsx"),

            new("RPT-PAYMENT-BATCH-SUMMARY", "Rezumat loturi plăți",
                "Sumar agregat al loturilor de plăți pe perioada cerută.",
                "finance", "Monthly", FromToWindowParams,
                "[\"BatchSqid\",\"PaymentCount\",\"TotalMdl\",\"GeneratedUtc\"]",
                RoleCodes.Admin, "0 0 6 1 * ?", CommonFormats, "PaymentsProcessed", "xlsx"),

            new("RPT-AGING-DOSSIERS", "Aging dosare deschise",
                "Distribuția vechimii dosarelor deschise în bucket-uri.",
                "supervisor", "Weekly", EmptyParams,
                "[\"Bucket\",\"Count\"]",
                RoleCodes.Decider, "0 0 7 ? * MON", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-DOC-VERDICT-MIX", "Distribuție verdicte documente",
                "Distribuția verdictelor pe documentele verificate.",
                "auditor", "Monthly", FromToWindowParams,
                "[\"Verdict\",\"Count\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "Statistical", "csv"),

            // ── Annex 6c (5). ──
            new("RPT-APPEAL-INBOX", "Coadă contestații",
                "Lista contestațiilor active.",
                RoleCodes.Decider, "Daily", EmptyParams,
                "[\"AppealSqid\",\"DossierSqid\",\"OpenedUtc\",\"Status\"]",
                RoleCodes.Decider, "0 0 5 * * ?", CommonFormats, "DecisionsIssued", "csv"),

            new("RPT-DOC-REQUESTS-CLOSED-RECENT", "Cereri documente închise recent",
                "Cereri externe închise în ultima fereastră.",
                "examiner", "Weekly", FromToWindowParams,
                "[\"RequestSqid\",\"DossierSqid\",\"ClosedUtc\"]",
                RoleCodes.Decider, "0 0 7 ? * MON", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-DOSSIER-ASSIGNMENTS-PER-EXAMINER", "Alocări dosare pe examinator",
                "Alocările active per examinator.",
                "supervisor", "Daily", EmptyParams,
                "[\"ExaminerSqid\",\"AssignedDossiers\"]",
                RoleCodes.Admin, "0 0 7 * * ?", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-PAYMENT-HISTORY", "Istoric plăți (dosar)",
                "Istoric cronologic al plăților pentru un dosar.",
                RoleCodes.Decider, "OnDemand",
                "{\"type\":\"object\",\"required\":[\"dossierSqid\"]," +
                "\"properties\":{\"dossierSqid\":{\"type\":\"string\"}}}",
                "[\"PaymentSqid\",\"AmountMdl\",\"PaidAtUtc\",\"Channel\"]",
                RoleCodes.Decider, "OnDemand", CommonFormats, "PaymentsProcessed", "csv"),

            new("RPT-DOSSIERS-BY-SERVICE", "Dosare pe serviciu",
                "Distribuția dosarelor active pe cod de serviciu.",
                "statistician", "Weekly", EmptyParams,
                "[\"ServiceCode\",\"Count\"]",
                RoleCodes.Admin, "0 0 7 ? * MON", CommonFormats, "Statistical", "csv"),

            // ── Annex 6d (5). ──
            new("RPT-DOSSIER-LIFECYCLE-TIME", "Timp ciclu de viață dosar",
                "Timpul mediu de la deschidere la închidere a dosarelor.",
                "supervisor", "Monthly", FromToWindowParams,
                "[\"ServiceCode\",\"AverageDays\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "PerformanceKpi", "xlsx"),

            new("RPT-EXAMINER-OUTCOMES", "Rezultate per examinator",
                "Distribuția rezultatelor (aprobat/respins) per examinator.",
                "supervisor", "Monthly", MonthUtcParams,
                "[\"ExaminerSqid\",\"Approved\",\"Rejected\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "PerformanceKpi", "xlsx"),

            new("RPT-NEW-APPLICATIONS-DAILY", "Cereri noi zilnice",
                "Numărul de cereri noi pe zi în fereastra dată.",
                "statistician", "Daily", FromToWindowParams,
                "[\"DateUtc\",\"Count\"]",
                RoleCodes.Decider, "0 0 5 * * ?", CommonFormats, "Statistical", "csv"),

            new("RPT-OUTSTANDING-AMOUNTS", "Sume restante",
                "Sume restante pe dosare cu plăți blocate.",
                "finance", "Weekly", EmptyParams,
                "[\"DossierSqid\",\"OutstandingMdl\"]",
                RoleCodes.Admin, "0 0 7 ? * MON", CommonFormats, "PaymentsProcessed", "xlsx"),

            new("RPT-DECISION-TURNAROUND", "Timp turnaround decizii",
                "Timpul mediu de la cerere la decizie.",
                "supervisor", "Monthly", MonthUtcParams,
                "[\"ServiceCode\",\"AverageDays\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "PerformanceKpi", "xlsx"),

            // ── Annex 6e (5). ──
            new("RPT-DOCUMENT-AGE-DISTRIBUTION", "Distribuție vârstă documente",
                "Vârsta documentelor stocate, în bucket-uri.",
                "archivist", "Quarterly", EmptyParams,
                "[\"Bucket\",\"Count\"]",
                RoleCodes.Admin, "0 0 7 1 */3 ?", CommonFormats, "Statistical", "csv"),

            new("RPT-REJECTION-REASONS", "Motive de respingere",
                "Distribuția motivelor de respingere a cererilor.",
                "supervisor", "Monthly", MonthUtcParams,
                "[\"Reason\",\"Count\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "DecisionsIssued", "csv"),

            new("RPT-MONTHLY-DECISIONS-BY-EXAMINER", "Decizii lunare per examinator",
                "Producția lunară a fiecărui examinator.",
                "supervisor", "Monthly", MonthUtcParams,
                "[\"ExaminerSqid\",\"DecisionCount\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "PerformanceKpi", "xlsx"),

            new("RPT-SLAS-MISSED", "SLA-uri ratate",
                "Lista dosarelor cu SLA depășit.",
                "supervisor", "Daily", EmptyParams,
                "[\"DossierSqid\",\"SlaName\",\"DaysOver\"]",
                RoleCodes.Admin, "0 0 5 * * ?", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-TOTAL-PAYMENTS-PER-MONTH", "Plăți totale pe lună",
                "Sumar lunar al plăților efectuate.",
                "finance", "Monthly", MonthUtcParams,
                "[\"MonthUtc\",\"TotalMdl\",\"Count\"]",
                RoleCodes.Admin, "0 0 6 1 * ?", CommonFormats, "PaymentsProcessed", "xlsx"),

            // ── Annex 6f (5). ──
            new("RPT-CASES-BY-AGE-GROUP", "Cazuri pe grupă de vârstă",
                "Distribuția cazurilor pe grupe de vârstă ale beneficiarilor.",
                "statistician", "Quarterly", EmptyParams,
                "[\"AgeGroup\",\"Count\"]",
                RoleCodes.Admin, "0 0 7 1 */3 ?", CommonFormats, "Statistical", "xlsx"),

            new("RPT-CASES-BY-LOCALITY", "Cazuri pe localitate",
                "Distribuția cazurilor pe localitatea beneficiarului.",
                "statistician", "Quarterly", EmptyParams,
                "[\"Locality\",\"Count\"]",
                RoleCodes.Admin, "0 0 7 1 */3 ?", CommonFormats, "Statistical", "xlsx"),

            new("RPT-EXAMINER-AVG-CASELOAD", "Volum mediu cazuri / examinator",
                "Volum mediu de cazuri active per examinator.",
                "supervisor", "Weekly", EmptyParams,
                "[\"ExaminerSqid\",\"AverageCaseload\"]",
                RoleCodes.Decider, "0 0 7 ? * MON", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-CANCELLATIONS-BY-REASON", "Anulări pe motiv",
                "Distribuția anulărilor de decizii pe motiv.",
                "supervisor", "Monthly", MonthUtcParams,
                "[\"Reason\",\"Count\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "DecisionsIssued", "csv"),

            new("RPT-DAILY-CASH-FLOW", "Flux numerar zilnic",
                "Sumar zilnic al fluxului de numerar (intrări/ieșiri).",
                "finance", "Daily", FromToWindowParams,
                "[\"DateUtc\",\"InflowMdl\",\"OutflowMdl\"]",
                RoleCodes.Admin, "0 0 5 * * ?", CommonFormats, "PaymentsProcessed", "xlsx"),

            // ── Annex 6g (5). ──
            new("RPT-DOCUMENT-TYPES-USAGE", "Utilizare tipuri documente",
                "Numărul de utilizări pe tip de document.",
                "archivist", "Monthly", MonthUtcParams,
                "[\"DocumentType\",\"UsageCount\"]",
                RoleCodes.Admin, "0 0 6 1 * ?", CommonFormats, "Statistical", "csv"),

            new("RPT-WORKFLOW-BACKLOG-AGE", "Vârstă coadă workflow",
                "Vârsta sarcinilor de workflow neîncheiate.",
                "supervisor", "Daily", EmptyParams,
                "[\"WorkflowCode\",\"AverageAgeHours\"]",
                RoleCodes.Decider, "0 0 5 * * ?", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-INSURED-PERSONS-NEW", "Persoane asigurate noi",
                "Persoane asigurate nou-înregistrate în fereastră.",
                "statistician", "Monthly", FromToWindowParams,
                "[\"InsuredPersonSqid\",\"FullName\",\"RegisteredUtc\"]",
                RoleCodes.Admin, "0 0 6 1 * ?", CommonFormats, "Contributions", "xlsx"),

            new("RPT-BENEFICIARIES-BY-SERVICE-TYPE", "Beneficiari pe tip de serviciu",
                "Numărul de beneficiari activi pe tipul de serviciu.",
                "statistician", "Monthly", EmptyParams,
                "[\"ServiceCode\",\"Count\"]",
                RoleCodes.Admin, "0 0 6 1 * ?", CommonFormats, "PaymentsProcessed", "csv"),

            new("RPT-NOTIFICATIONS-DELIVERY", "Livrare notificări",
                "Statistica livrării notificărilor către cetățeni.",
                "ops", "Weekly", FromToWindowParams,
                "[\"Channel\",\"Sent\",\"Delivered\",\"Failed\"]",
                RoleCodes.Admin, "0 0 7 ? * MON", CommonFormats, "PerformanceKpi", "csv"),

            // ── Annex 6h (5). ──
            new("RPT-AUDIT-EVENTS-BY-SEVERITY", "Evenimente audit pe severitate",
                "Distribuția evenimentelor de audit pe severitate.",
                "auditor", "Monthly", FromToWindowParams,
                "[\"Severity\",\"Count\"]",
                RoleCodes.TechAdmin, "0 0 6 1 * ?", CommonFormats, "AuditSecurity", "csv"),

            new("RPT-DOCUMENT-UPLOAD-VOLUMES", "Volume upload documente",
                "Volume zilnice de încărcări de documente.",
                "ops", "Weekly", FromToWindowParams,
                "[\"DateUtc\",\"Count\",\"TotalSizeBytes\"]",
                RoleCodes.Admin, "0 0 7 ? * MON", CommonFormats, "PerformanceKpi", "xlsx"),

            new("RPT-LOGIN-EVENTS-PER-DAY", "Login-uri pe zi",
                "Numărul de evenimente de autentificare pe zi.",
                "security", "Daily", FromToWindowParams,
                "[\"DateUtc\",\"Success\",\"Failure\"]",
                RoleCodes.TechAdmin, "0 0 5 * * ?", CommonFormats, "AuditSecurity", "csv"),

            new("RPT-ACTIVE-USERS-LAST-30D", "Utilizatori activi (30 zile)",
                "Utilizatorii activi în ultimele 30 de zile.",
                "ops", "Weekly", EmptyParams,
                "[\"UserSqid\",\"LastSeenUtc\",\"SessionCount\"]",
                RoleCodes.TechAdmin, "0 0 7 ? * MON", CommonFormats, "AuditSecurity", "csv"),

            new("RPT-DOSSIER-EXAMINATION-DURATION", "Durată examinare dosar",
                "Distribuția duratei de examinare a dosarelor.",
                "supervisor", "Monthly", MonthUtcParams,
                "[\"Bucket\",\"Count\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "PerformanceKpi", "csv"),

            // ── Annex 6i (5). ──
            new("RPT-PASSPORT-USAGE", "Utilizare paspoarte servicii",
                "Numărul de cereri per passport (cod serviciu).",
                "statistician", "Monthly", MonthUtcParams,
                "[\"PassportCode\",\"Count\"]",
                RoleCodes.Admin, "0 0 6 1 * ?", CommonFormats, "Statistical", "csv"),

            new("RPT-AUDIT-EVENTS-BY-ACTION", "Evenimente audit pe acțiune",
                "Distribuția evenimentelor de audit pe codul de acțiune.",
                "auditor", "Monthly", FromToWindowParams,
                "[\"ActionCode\",\"Count\"]",
                RoleCodes.TechAdmin, "0 0 6 1 * ?", CommonFormats, "AuditSecurity", "csv"),

            new("RPT-NOTIFICATIONS-UNREAD", "Notificări necitite",
                "Notificările necitite curente per utilizator.",
                "ops", "Daily", EmptyParams,
                "[\"UserSqid\",\"UnreadCount\"]",
                RoleCodes.Admin, "0 0 5 * * ?", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-DOCUMENTS-UNSIGNED", "Documente nesemnate",
                "Lista documentelor în așteptare de semnătură.",
                "supervisor", "Daily", EmptyParams,
                "[\"DocumentSqid\",\"DossierSqid\",\"PendingSinceUtc\"]",
                RoleCodes.Decider, "0 0 5 * * ?", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-DOSSIERS-OPEN-BY-EXAMINER", "Dosare deschise pe examinator",
                "Dosare deschise alocate fiecărui examinator.",
                "supervisor", "Daily", EmptyParams,
                "[\"ExaminerSqid\",\"OpenCount\"]",
                RoleCodes.Decider, "0 0 5 * * ?", CommonFormats, "PerformanceKpi", "csv"),

            // ── Annex 6j (5). ──
            new("RPT-DOSSIERS-CLOSED-BY-OUTCOME", "Dosare închise pe rezultat",
                "Numărul de dosare închise pe rezultat (aprobat/respins/anulat).",
                "supervisor", "Monthly", FromToWindowParams,
                "[\"Outcome\",\"Count\"]",
                RoleCodes.Decider, "0 0 6 1 * ?", CommonFormats, "DecisionsIssued", "csv"),

            new("RPT-APPLICATIONS-BY-PASSPORT-MONTHLY", "Cereri lunare per passport",
                "Distribuția lunară a cererilor per passport.",
                "statistician", "Monthly", MonthUtcParams,
                "[\"PassportCode\",\"Count\"]",
                RoleCodes.Admin, "0 0 6 1 * ?", CommonFormats, "Statistical", "xlsx"),

            new("RPT-NOTIFICATIONS-BY-CITIZEN", "Notificări pe cetățean",
                "Numărul de notificări trimise per cetățean.",
                "ops", "Quarterly", FromToWindowParams,
                "[\"CitizenSqid\",\"NotificationCount\"]",
                RoleCodes.Admin, "0 0 7 1 */3 ?", CommonFormats, "PerformanceKpi", "csv"),

            new("RPT-AUDIT-EVENTS-BY-ACTOR", "Evenimente audit pe actor",
                "Distribuția evenimentelor de audit pe identitatea actorului.",
                "auditor", "Monthly", FromToWindowParams,
                "[\"ActorId\",\"Count\"]",
                RoleCodes.TechAdmin, "0 0 6 1 * ?", CommonFormats, "AuditSecurity", "csv"),

            new("RPT-DOCUMENT-VERDICTS-OVER-TIME", "Verdicte documente în timp",
                "Evoluția verdictelor pe documente în timp.",
                "auditor", "Monthly", FromToWindowParams,
                "[\"PeriodUtc\",\"Verdict\",\"Count\"]",
                RoleCodes.TechAdmin, "0 0 6 1 * ?", CommonFormats, "AuditSecurity", "xlsx"),
        };

        return list.AsReadOnly();
    }
}
