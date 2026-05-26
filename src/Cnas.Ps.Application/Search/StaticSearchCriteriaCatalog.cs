using System.Collections.Generic;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Search;

/// <summary>
/// R0501 / TOR CF 01.04 — default <see cref="ISearchCriteriaCatalog"/>
/// implementation. Returns a hard-coded but well-documented descriptor list
/// per canonical unified domain. The catalogue evolves via code review only —
/// administrative editing of the descriptors is intentionally out of scope
/// for this iteration (a future ticket may move the catalogue into a
/// nomenclator-style classifier so non-developers can curate it).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> Stateless and thread-safe — registered as a singleton
/// in <c>InfrastructureServiceCollectionExtensions</c>.
/// </para>
/// <para>
/// <b>Adding new domains.</b> Append a case to <see cref="GetCriteriaFor"/>
/// and add the domain code to <see cref="GlobalSearchDomains.Unified"/>.
/// </para>
/// </remarks>
public sealed class StaticSearchCriteriaCatalog : ISearchCriteriaCatalog
{
    /// <summary>Shared singleton "no matching domain" empty list.</summary>
    private static readonly IReadOnlyList<SearchCriterionDescriptor> Empty =
        new List<SearchCriterionDescriptor>(0);

    /// <inheritdoc />
    public IReadOnlyList<SearchCriterionDescriptor> GetCriteriaFor(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return Empty;
        }
        return domain switch
        {
            GlobalSearchDomains.Applications => ApplicationsCriteria,
            GlobalSearchDomains.Applicants => ApplicantsCriteria,
            GlobalSearchDomains.Contributors => ContributorsCriteria,
            GlobalSearchDomains.Payers => ContributorsCriteria, // aliased
            GlobalSearchDomains.InsuredPersons => InsuredPersonsCriteria,
            GlobalSearchDomains.Dossiers => DossiersCriteria,
            GlobalSearchDomains.Tasks => TasksCriteria,
            GlobalSearchDomains.Notifications => NotificationsCriteria,
            GlobalSearchDomains.Documents => DocumentsCriteria,
            GlobalSearchDomains.IssuedDocuments => DocumentsCriteria, // aliased
            GlobalSearchDomains.WorkflowDocuments => DocumentsCriteria, // aliased
            _ => Empty,
        };
    }

    // ─────────────────────── per-domain descriptor tables ───────────────────────
    //
    // iter 125 / R0521 — every descriptor below now carries an explicit
    // SearchCriterionMetadataKind (full-text / date-range / classifier / status
    // / keyword / *Metadata) so the UI can group criteria into the CF 03.02
    // form sections. Backward compatibility note: the wire shape of every
    // pre-existing descriptor is unchanged — the new MetadataKind field is an
    // additive Public property and the JSON property names + values for every
    // other field are byte-identical.
    //
    // Cross-domain invariants enforced by SearchCriteriaCatalogMetadataTests:
    //   • Every Unified domain publishes at least one FullText + one DateRange
    //     + one Status criterion (the CF 03.02 baseline).
    //   • Every classifier criterion advertises ClassifierValue AND a non-null
    //     LookupSource so the UI can fetch the allowed code list.
    //   • Domains that own a privileged metadata column carry the matching
    //     *Metadata kind (Applications → Service + Applicant,
    //      Contributors → Payer, InsuredPersons → Insured,
    //      Documents → Document).

    /// <summary>Applications (Cereri) criteria.</summary>
    private static readonly IReadOnlyList<SearchCriterionDescriptor> ApplicationsCriteria =
    [
        // ApplicationNumber (free-text) — the headline keyword you type to find an application.
        new("code", "search.applications.code", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.Equals, SearchOperatorKind.StartsWith }, null,
            SearchCriterionMetadataKind.ApplicationMetadata),
        // Lifecycle status — Draft / Submitted / UnderExamination / Approved / Rejected.
        new("status", "search.applications.status", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.Status),
        // Dossier identifier (Keyword) — the dossier this application belongs to.
        new("dossierNumber", "search.applications.dossier", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.Keyword),
        // Free-text applicant search (matches DisplayName / family name).
        new("applicantName", "search.applications.applicantName", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.StartsWith }, null,
            SearchCriterionMetadataKind.FullText),
        // Applicant IDNP (privileged ApplicantMetadata; exact-match only).
        new("applicantIdnp", "search.applications.applicantIdnp", SearchDataType.String,
            new[] { SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.ApplicantMetadata),
        // Service-catalogue passport code (privileged ServiceMetadata).
        new("servicePassportCode", "search.applications.servicePassportCode", SearchDataType.String,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.ServiceMetadata),
        // From-date and to-date pair making up the DateRange section.
        new("dateFrom", "search.applications.dateFrom", SearchDataType.Date,
            new[] { SearchOperatorKind.Range, SearchOperatorKind.Comparison }, null,
            SearchCriterionMetadataKind.DateRange),
        new("dateTo", "search.applications.dateTo", SearchDataType.Date,
            new[] { SearchOperatorKind.Range, SearchOperatorKind.Comparison }, null,
            SearchCriterionMetadataKind.DateRange),
    ];

    /// <summary>Applicants (Solicitanți) criteria.</summary>
    private static readonly IReadOnlyList<SearchCriterionDescriptor> ApplicantsCriteria =
    [
        new("name", "search.applicants.name", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.StartsWith }, null,
            SearchCriterionMetadataKind.FullText),
        // Solicitant kind = Natural / Legal — used as the "status" facet for this domain.
        new("kind", "search.applicants.kind", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.Status),
        // Applicant IDNP (privileged ApplicantMetadata).
        new("idnp", "search.applicants.idnp", SearchDataType.String,
            new[] { SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.ApplicantMetadata),
        new("region", "search.applicants.region", SearchDataType.Classifier,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, "REGION",
            SearchCriterionMetadataKind.ClassifierValue),
        new("registeredFrom", "search.applicants.registeredFrom", SearchDataType.Date,
            new[] { SearchOperatorKind.Range, SearchOperatorKind.Comparison }, null,
            SearchCriterionMetadataKind.DateRange),
    ];

    /// <summary>Contributors / Payers (Plătitori) criteria.</summary>
    private static readonly IReadOnlyList<SearchCriterionDescriptor> ContributorsCriteria =
    [
        // ContributorCode (PayerMetadata) — privileged payer identifier.
        new("code", "search.contributors.code", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.PayerMetadata),
        new("idno", "search.contributors.idno", SearchDataType.String,
            new[] { SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.Keyword),
        new("name", "search.contributors.name", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.StartsWith }, null,
            SearchCriterionMetadataKind.FullText),
        new("status", "search.contributors.status", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.Status),
        // CAEM REV-2 economic-activity classifier.
        new("caem", "search.contributors.caem", SearchDataType.Classifier,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, "CAEM_REV2",
            SearchCriterionMetadataKind.ClassifierValue),
        // CUATM administrative-territory classifier (locality).
        new("cuatm", "search.contributors.cuatm", SearchDataType.Classifier,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, "CUATM",
            SearchCriterionMetadataKind.ClassifierValue),
        new("registeredFrom", "search.contributors.registeredFrom", SearchDataType.Date,
            new[] { SearchOperatorKind.Range, SearchOperatorKind.Comparison }, null,
            SearchCriterionMetadataKind.DateRange),
    ];

    /// <summary>Insured persons (Persoane asigurate) criteria.</summary>
    private static readonly IReadOnlyList<SearchCriterionDescriptor> InsuredPersonsCriteria =
    [
        // Privileged InsuredMetadata (Idnp).
        new("idnp", "search.insured.idnp", SearchDataType.String,
            new[] { SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.InsuredMetadata),
        new("name", "search.insured.name", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.StartsWith }, null,
            SearchCriterionMetadataKind.FullText),
        new("employer", "search.insured.employer", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.Keyword),
        // IsDeceased flag — surfaced as a boolean-status facet.
        new("status", "search.insured.status", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.Status),
        new("birthDateFrom", "search.insured.birthFrom", SearchDataType.Date,
            new[] { SearchOperatorKind.Range, SearchOperatorKind.Comparison }, null,
            SearchCriterionMetadataKind.DateRange),
    ];

    /// <summary>Dossiers (Dosare) criteria.</summary>
    private static readonly IReadOnlyList<SearchCriterionDescriptor> DossiersCriteria =
    [
        new("number", "search.dossiers.number", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.FullText),
        new("examiner", "search.dossiers.examiner", SearchDataType.String,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.Contains }, null,
            SearchCriterionMetadataKind.UserMetadata),
        // Privileged ServiceMetadata — dossier carries the service passport.
        new("servicePassportCode", "search.dossiers.servicePassportCode", SearchDataType.String,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.ServiceMetadata),
        new("status", "search.dossiers.status", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.Status),
        new("dateFrom", "search.dossiers.dateFrom", SearchDataType.Date,
            new[] { SearchOperatorKind.Range, SearchOperatorKind.Comparison }, null,
            SearchCriterionMetadataKind.DateRange),
    ];

    /// <summary>Workflow tasks (Sarcini) criteria.</summary>
    private static readonly IReadOnlyList<SearchCriterionDescriptor> TasksCriteria =
    [
        // Title — the only free-text field on a task.
        new("title", "search.tasks.title", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.StartsWith }, null,
            SearchCriterionMetadataKind.FullText),
        new("status", "search.tasks.status", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.Status),
        // Assignee — sourced from the user table (UserMetadata).
        new("assignee", "search.tasks.assignee", SearchDataType.String,
            new[] { SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.UserMetadata),
        new("priority", "search.tasks.priority", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.Keyword),
        new("dueFrom", "search.tasks.dueFrom", SearchDataType.Date,
            new[] { SearchOperatorKind.Range, SearchOperatorKind.Comparison }, null,
            SearchCriterionMetadataKind.DateRange),
    ];

    /// <summary>Notifications criteria.</summary>
    private static readonly IReadOnlyList<SearchCriterionDescriptor> NotificationsCriteria =
    [
        new("subject", "search.notifications.subject", SearchDataType.String,
            new[] { SearchOperatorKind.Contains }, null,
            SearchCriterionMetadataKind.FullText),
        new("channel", "search.notifications.channel", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals }, null,
            SearchCriterionMetadataKind.Keyword),
        new("status", "search.notifications.status", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.Status),
        new("sentFrom", "search.notifications.sentFrom", SearchDataType.Date,
            new[] { SearchOperatorKind.Range, SearchOperatorKind.Comparison }, null,
            SearchCriterionMetadataKind.DateRange),
    ];

    /// <summary>Documents / Issued / Workflow documents criteria.</summary>
    private static readonly IReadOnlyList<SearchCriterionDescriptor> DocumentsCriteria =
    [
        new("title", "search.documents.title", SearchDataType.String,
            new[] { SearchOperatorKind.Contains, SearchOperatorKind.StartsWith }, null,
            SearchCriterionMetadataKind.FullText),
        // DocumentTypeCode (privileged DocumentMetadata) — Decision / Refusal / etc.
        new("documentTypeCode", "search.documents.typeCode", SearchDataType.String,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.DocumentMetadata),
        new("kind", "search.documents.kind", SearchDataType.Enum,
            new[] { SearchOperatorKind.Equals, SearchOperatorKind.InSet }, null,
            SearchCriterionMetadataKind.Status),
        new("dateFrom", "search.documents.dateFrom", SearchDataType.Date,
            new[] { SearchOperatorKind.Range, SearchOperatorKind.Comparison }, null,
            SearchCriterionMetadataKind.DateRange),
    ];
}
