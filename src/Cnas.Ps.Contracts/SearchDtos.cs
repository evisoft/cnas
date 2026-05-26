using Cnas.Ps.Contracts.Security;

namespace Cnas.Ps.Contracts;

/// <summary>
/// R0160 / R0161 / TOR CF 03.03 — input envelope for the global full-text-search
/// surface (<c>IGlobalSearchService</c>). Carries the free-text query, an optional
/// per-domain filter, and paging.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a new DTO when the codebase already ships <c>FullTextSearchResultDto</c>.</b>
/// The pre-existing <c>FullTextSearchResultDto</c> in <c>Cnas.Ps.Contracts.Search</c>
/// (R0522) is the engine-adapter contract — a single index's ids + total count.
/// The R0160/R0161 surface returns a richer, cross-domain payload (per-hit Domain,
/// Title, Snippet, and Rank); collapsing the two would force callers to switch on
/// magic field-presence to interpret an "engine result" vs a "global search result".
/// Keeping the names distinct preserves the per-batch refactor seam.
/// </para>
/// <para>
/// <b>Known domain codes.</b> <see cref="Domains"/> entries are matched
/// case-insensitively against the canonical set: <c>"applications"</c>,
/// <c>"contributors"</c>, <c>"insured-persons"</c>, <c>"documents"</c>,
/// <c>"dossiers"</c>. An empty / null list means "search every known domain" — the
/// service fans out to all five.
/// </para>
/// </remarks>
/// <param name="Query">Free-text query (1..256 chars, no leading/trailing whitespace).</param>
/// <param name="Domains">
/// Optional case-insensitive list of domain codes to restrict the search to. Empty /
/// null = all domains.
/// </param>
/// <param name="Skip">Zero-based skip count (paging). Must be ≥ 0.</param>
/// <param name="Take">Page size. Must be 1..100; the service hard-caps at 100.</param>
public sealed record GlobalSearchInputDto(
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Query,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    IReadOnlyList<string>? Domains,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take);

/// <summary>
/// R0160 / R0161 / TOR CF 03.03 — wire response for the global full-text-search
/// surface. Carries the merged + globally-ranked hit list, the grand total across
/// every queried domain, and the echoed paging so the SPA can render the paging
/// controls without a second round trip.
/// </summary>
/// <param name="TotalHits">Total hits across every queried domain BEFORE paging.</param>
/// <param name="Results">Paged hit list, sorted by descending <c>Rank</c>.</param>
/// <param name="Skip">Echoed skip count.</param>
/// <param name="Take">Echoed take count.</param>
public sealed record GlobalSearchResultDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int TotalHits,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<GlobalSearchHitDto> Results,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Skip,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    int Take);

/// <summary>
/// R0160 / R0161 / TOR CF 03.03 — one match in the global full-text-search result
/// list. Carries the originating domain (so the UI can branch on the icon / link
/// target), the Sqid-encoded id of the underlying row (CLAUDE.md RULE 3), a
/// human-readable title, a short snippet, and the engine-supplied relevance rank.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rank semantics.</b> On the Postgres path <see cref="Rank"/> is
/// <c>ts_rank_cd(search_vector, plainto_tsquery(...))</c> — strictly greater than
/// zero for any match. On the InMemory fallback rank is the lowercase-substring
/// occurrence count divided by the haystack length so the relative ordering is
/// stable across the two providers (rank is not directly comparable across
/// providers; do not surface absolute values to operators).
/// </para>
/// </remarks>
/// <param name="Domain">
/// Stable lower-kebab-case domain code (e.g. <c>"applications"</c>). Used by the
/// UI to render the per-row icon and the click-through route. Public because the
/// domain label itself carries no PII.
/// </param>
/// <param name="Sqid">
/// Sqid-encoded id of the underlying row (CLAUDE.md RULE 3). Public — the encoded
/// id is opaque and does not leak business volume.
/// </param>
/// <param name="Title">Best human-readable title for the row (Internal — may carry low-PII names).</param>
/// <param name="Snippet">Short surrounding text excerpt (Internal — may carry low-PII context).</param>
/// <param name="Rank">Engine-supplied relevance rank; larger is better.</param>
public sealed record GlobalSearchHitDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Domain,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Snippet,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    double Rank);

/// <summary>
/// R0160 / R0161 — stable catalogue of domain codes the global search surface
/// recognises. Centralised so the validator, the service, and the integration
/// tests agree on the spelling.
/// </summary>
public static class GlobalSearchDomains
{
    /// <summary>Applications (Cereri) — searches <c>ReferenceNumber</c>.</summary>
    public const string Applications = "applications";

    /// <summary>Contributors (Payers / Payers de cotizatii) — searches <c>Idno</c> + <c>Denumire</c>.</summary>
    public const string Contributors = "contributors";

    /// <summary>Insured persons — searches <c>LastName</c> + <c>FirstName</c> + <c>Idnp</c>.</summary>
    public const string InsuredPersons = "insured-persons";

    /// <summary>Documents — searches <c>Title</c> + <c>VerdictNote</c>.</summary>
    public const string Documents = "documents";

    /// <summary>Dossiers (Dosare) — searches <c>DossierNumber</c>.</summary>
    public const string Dossiers = "dossiers";

    /// <summary>
    /// R0520 / TOR CF 03.01 — Solicitants (applicants/payers natural persons) — searches
    /// <c>DisplayName</c> + <c>Email</c> (encrypted IDNP is excluded from query text).
    /// </summary>
    public const string Applicants = "applicants";

    /// <summary>
    /// R0520 / TOR CF 03.01 — Payer registry rows (Plătitori) — searches <c>Idno</c>
    /// and friendly bank-account labels. Currently aliased to the same registry as
    /// <see cref="Contributors"/>, but exposed as a distinct domain code so the UI can
    /// render the per-domain icon.
    /// </summary>
    public const string Payers = "payers";

    /// <summary>R0520 / TOR CF 03.01 — Workflow tasks (Sarcini) — searches <c>Title</c>.</summary>
    public const string Tasks = "tasks";

    /// <summary>R0520 / TOR CF 03.01 — Notifications (Notificări) — searches <c>Subject</c> + <c>Body</c>.</summary>
    public const string Notifications = "notifications";

    /// <summary>
    /// R0520 / TOR CF 03.01 — issued workflow documents (decisions / certificates produced by
    /// CNAS). Currently aliased to the same physical table as <see cref="Documents"/>; exposed
    /// as a distinct code so future projector code can specialise by <c>DocumentKind</c>.
    /// </summary>
    public const string IssuedDocuments = "issued-documents";

    /// <summary>
    /// R0520 / TOR CF 03.01 — workflow-attached documents (input attachments / dossier
    /// scans). Currently aliased to the same physical table as <see cref="Documents"/>; exposed
    /// as a distinct code so future projector code can specialise on the attachment side.
    /// </summary>
    public const string WorkflowDocuments = "workflow-documents";

    /// <summary>The five legacy canonical domain codes accepted by the v1 search surface.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Applications,
        Contributors,
        InsuredPersons,
        Documents,
        Dossiers,
    ];

    /// <summary>
    /// R0520 — full nine-domain unified catalogue. Used by the unified search projection
    /// and by the metadata-driven criteria catalogue (R0501).
    /// </summary>
    public static readonly IReadOnlyList<string> Unified =
    [
        Applicants,
        Applications,
        Dossiers,
        Payers,
        InsuredPersons,
        Tasks,
        Notifications,
        IssuedDocuments,
        WorkflowDocuments,
    ];
}

/// <summary>
/// R0501 / TOR CF 01.04 — the data type of a single criterion exposed by
/// <c>ISearchCriteriaCatalog</c>. The UI uses this to render the correct input
/// widget (text box, date picker, classifier drop-down, etc.) and to coerce the
/// posted value before sending it to the search service.
/// </summary>
/// <remarks>
/// The member names are deliberately the primitive type names ("String",
/// "Integer", "Decimal", "DateTime") so the wire JSON ("dataType":"String")
/// is self-describing. CA1720 is suppressed: these are stable contract values
/// part of the API, not C# type aliases.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1720:Identifier contains type name",
    Justification = "Stable wire-contract values — name renames are breaking.")]
public enum SearchDataType
{
    /// <summary>Free-text string (LIKE / ILIKE-able).</summary>
    String = 0,

    /// <summary>Integer (whole number).</summary>
    Integer = 1,

    /// <summary>Decimal number (currency / quantity).</summary>
    Decimal = 2,

    /// <summary>Calendar date (no time component).</summary>
    Date = 3,

    /// <summary>UTC instant (date + time).</summary>
    DateTime = 4,

    /// <summary>Enumerated value with a fixed code list (status / kind).</summary>
    Enum = 5,

    /// <summary>
    /// Reference to a classifier code (Nomenclator). The <c>LookupSource</c> field
    /// names the classifier scheme so the UI can fetch its allowed values.
    /// </summary>
    Classifier = 6,

    /// <summary>Boolean (true / false).</summary>
    Boolean = 7,
}

/// <summary>
/// R0501 / TOR CF 01.04 — the operator the UI may apply to a single criterion.
/// Mapped 1-1 onto the search service's filter clauses; the catalogue declares
/// which operators are <i>allowed</i> per field so a malformed combination is
/// caught at the UI before a round-trip.
/// </summary>
public enum SearchOperatorKind
{
    /// <summary>Strict equality. Applicable to every <see cref="SearchDataType"/>.</summary>
    Equals = 0,

    /// <summary>String "contains" / SQL ILIKE. Applicable to <see cref="SearchDataType.String"/> only.</summary>
    Contains = 1,

    /// <summary>String "starts with". Applicable to <see cref="SearchDataType.String"/> only.</summary>
    StartsWith = 2,

    /// <summary>Inclusive range (between two values). Applicable to numbers and dates.</summary>
    Range = 3,

    /// <summary>Greater-than / less-than comparison. Applicable to numbers and dates.</summary>
    Comparison = 4,

    /// <summary>"In set" — membership test against a discrete list. Applicable to enums and classifiers.</summary>
    InSet = 5,
}

/// <summary>
/// R0521 / TOR CF 03.02 — taxonomy of criterion <i>kinds</i> the catalogue may
/// publish. Whereas <see cref="SearchDataType"/> describes the primitive value
/// shape (string / int / date / classifier / etc.) the <i>kind</i> describes
/// the <b>semantic role</b> the criterion plays in the CF 03.02 register-browser
/// UI: free-text vs date-range vs classifier vs status vs keyword vs a piece of
/// metadata coming from a specific upstream entity (applicant, application,
/// payer, insured person, document, user, service).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second axis in addition to <see cref="SearchDataType"/>.</b> Several
/// "Classifier" criteria reference different upstream entities (CAEM vs CUATM vs
/// region), and a date field can be either a generic date filter or part of a
/// from/to date-range section. CF 03.02 explicitly enumerates the categories the
/// UI must surface — collapsing them onto the primitive data-type axis loses
/// that intent.
/// </para>
/// <para>
/// <b>Stable wire shape.</b> Member names are enum-name strings on the JSON
/// wire (e.g. <c>"FullText"</c>, <c>"DocumentMetadata"</c>). Renaming a member
/// is a breaking change to every saved-search row that references it.
/// </para>
/// </remarks>
public enum SearchCriterionMetadataKind
{
    /// <summary>Free-text search criterion (e.g. applicant name, document title).</summary>
    FullText = 0,

    /// <summary>From/to date-range criterion (e.g. submission date, decision date).</summary>
    DateRange = 1,

    /// <summary>
    /// Reference to a classifier code (Nomenclator) — the <c>LookupSource</c>
    /// names the scheme (CAEM_REV2 / CUATM / REGION / etc.).
    /// </summary>
    ClassifierValue = 2,

    /// <summary>Status / lifecycle stage criterion (enum-valued).</summary>
    Status = 3,

    /// <summary>Exact-equality keyword criterion (e.g. reference number, dossier code).</summary>
    Keyword = 4,

    /// <summary>Metadata sourced from the system user table (DisplayName / Email).</summary>
    UserMetadata = 5,

    /// <summary>Metadata sourced from the service catalogue (PassportCode).</summary>
    ServiceMetadata = 6,

    /// <summary>Metadata sourced from the applicant entity (Idno / Idnp).</summary>
    ApplicantMetadata = 7,

    /// <summary>Metadata sourced from the application entity (ApplicationNumber).</summary>
    ApplicationMetadata = 8,

    /// <summary>Metadata sourced from the payer / contributor registry (ContributorCode).</summary>
    PayerMetadata = 9,

    /// <summary>Metadata sourced from the insured-person registry (Idnp).</summary>
    InsuredMetadata = 10,

    /// <summary>Metadata sourced from the documents registry (DocumentTypeCode).</summary>
    DocumentMetadata = 11,
}

/// <summary>
/// R0501 / TOR CF 01.04 — descriptor for a single criterion exposed by
/// <c>ISearchCriteriaCatalog</c>. The catalogue publishes one entry per
/// search-addressable field per domain so the UI can build a metadata-driven
/// query-by-example form without server-side hard-coding.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a descriptor record, not a per-domain DTO.</b> The criteria for each
/// domain (applications, contributors, etc.) differ in shape but share the same
/// abstract metadata (field name + data type + allowed operators + optional
/// classifier source). A single descriptor record collapses the per-domain DTO
/// explosion into one polymorphic shape and lets the UI iterate uniformly.
/// </para>
/// <para>
/// <b>Stable wire shape.</b> Field codes use lowerCamelCase to match the JSON
/// serialisation convention (e.g. <c>"applicantName"</c> not
/// <c>"ApplicantName"</c>). Renaming a field code is a breaking change to every
/// saved-search row that references it.
/// </para>
/// </remarks>
/// <param name="Field">
/// Stable lowerCamelCase code identifying the field within its domain. Public
/// because the code itself is metadata — no PII.
/// </param>
/// <param name="DisplayLabel">
/// Translation-key label the UI should show for this criterion (the actual
/// localisation happens at the presentation layer). Public.
/// </param>
/// <param name="DataType">The data type of values the field accepts.</param>
/// <param name="Operators">
/// The operators the UI may apply against this field. At least one entry is
/// guaranteed; the first entry is the catalogue's recommended default.
/// </param>
/// <param name="LookupSource">
/// When <see cref="DataType"/> is <see cref="SearchDataType.Classifier"/>, the
/// classifier scheme code (e.g. <c>"CAEM"</c>, <c>"REGION"</c>) the UI fetches
/// allowed values from; <see langword="null"/> for all other data types.
/// </param>
/// <param name="MetadataKind">
/// R0521 / TOR CF 03.02 — semantic role of the criterion (full-text /
/// date-range / classifier / status / keyword / piece of upstream metadata).
/// The UI uses this to group criteria into the CF 03.02 form sections; the
/// service-layer validator may use it to apply per-kind defaults.
/// </param>
public sealed record SearchCriterionDescriptor(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Field,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string DisplayLabel,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    SearchDataType DataType,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    IReadOnlyList<SearchOperatorKind> Operators,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string? LookupSource,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    SearchCriterionMetadataKind MetadataKind = SearchCriterionMetadataKind.FullText);

/// <summary>
/// R0520 / TOR CF 03.01 — single hit returned by the unified cross-entity search
/// service. Carries a homogeneous projection of all nine canonical domains
/// (applicants, applications, dossiers, payers, insured persons, tasks,
/// notifications, issued documents, workflow documents) so the UI can render
/// every hit with the same template regardless of underlying entity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Difference from <c>GlobalSearchHitDto</c>.</b> The legacy DTO covers five
/// domains and exposes only <c>Title</c> + <c>Snippet</c>. The unified shape
/// adds <c>Subtitle</c>, <c>Url</c>, and <c>Highlights</c> so the UI can render
/// a richer two-line list item without a second fetch.
/// </para>
/// <para>
/// <b>Rank semantics.</b> The <see cref="RelevanceScore"/> is provider-local —
/// do not surface its absolute value to operators; only the relative ordering
/// across rows in the SAME response is meaningful.
/// </para>
/// </remarks>
/// <param name="Domain">
/// Stable lower-kebab-case domain code from <see cref="GlobalSearchDomains"/>.
/// </param>
/// <param name="Sqid">Sqid-encoded id of the underlying row (CLAUDE.md RULE 3).</param>
/// <param name="Title">Primary human-readable label (line 1 in the UI).</param>
/// <param name="Subtitle">
/// Secondary qualifier line (line 2 in the UI) — e.g. the dossier number for
/// an application hit, or the assigned examiner for a task hit. May be empty.
/// </param>
/// <param name="Snippet">
/// Short surrounding text excerpt — kept for backward compatibility with the
/// legacy <c>GlobalSearchHitDto</c> consumers; the unified UI prefers
/// <see cref="Subtitle"/>.
/// </param>
/// <param name="Url">
/// Deep-link path the SPA should navigate to when the user clicks the hit.
/// Always begins with a leading slash (e.g. <c>/applications/k3Gq9</c>).
/// </param>
/// <param name="RelevanceScore">
/// Engine-supplied relevance score; larger is better. Same conventions as
/// <c>GlobalSearchHitDto.Rank</c>.
/// </param>
/// <param name="Highlights">
/// Per-field snippet fragments the UI may render bold to draw attention to the
/// match location. Always non-null (empty list when the engine did not supply
/// highlights).
/// </param>
public sealed record UnifiedSearchHitDto(
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Domain,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Sqid,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Title,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Subtitle,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    string Snippet,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    string Url,
    [property: SensitivityClassification(SensitivityLabel.Public)]
    double RelevanceScore,
    [property: SensitivityClassification(SensitivityLabel.Internal)]
    IReadOnlyList<string> Highlights);
