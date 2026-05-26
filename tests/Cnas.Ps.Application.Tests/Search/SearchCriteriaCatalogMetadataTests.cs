using System.Linq;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Search;

/// <summary>
/// R0521 / TOR CF 03.02 — iter 125 — additional unit coverage for
/// <see cref="ISearchCriteriaCatalog"/> implementations that asserts the
/// full criteria taxonomy (full-text, dates, classifier values, statuses,
/// keywords, plus user / service / applicant / application / payer /
/// insured / document metadata) is present per domain. The catalogue
/// publishes <see cref="SearchCriterionDescriptor.MetadataKind"/> so the
/// UI can group criteria into form sections matching CF 03.02.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate file from <see cref="SearchCriteriaCatalogTests"/>.</b>
/// The pre-existing file pins the core field list per domain; this file
/// pins the metadata-kind taxonomy added in iter 125. Keeping them
/// separate isolates the per-iter contract delta and keeps the test
/// fixtures focussed.
/// </para>
/// </remarks>
public sealed class SearchCriteriaCatalogMetadataTests
{
    /// <summary>Returns the production registry under test.</summary>
    /// <returns>The wired catalogue.</returns>
    private static ISearchCriteriaCatalog NewCatalog() => new StaticSearchCriteriaCatalog();

    /// <summary>
    /// The applications domain MUST carry both a service-metadata criterion
    /// (PassportCode) and an applicant-metadata criterion (Idnp) per CF 03.02.
    /// </summary>
    [Fact]
    public void GetCriteriaFor_Applications_CarriesServiceAndApplicantMetadata()
    {
        var catalog = NewCatalog();

        var result = catalog.GetCriteriaFor(GlobalSearchDomains.Applications);

        result.Should().Contain(c => c.MetadataKind == SearchCriterionMetadataKind.ServiceMetadata,
            "applications carry the service passport code (CF 03.02)");
        result.Should().Contain(c => c.MetadataKind == SearchCriterionMetadataKind.ApplicantMetadata,
            "applications expose the applicant Idnp (CF 03.02)");
    }

    /// <summary>
    /// Every canonical Unified domain MUST publish at least one full-text
    /// criterion, one date-range criterion, and one status criterion — the
    /// three baseline taxonomies CF 03.02 calls out for every register.
    /// </summary>
    [Fact]
    public void GetCriteriaFor_EveryUnifiedDomain_CarriesFullTextDateRangeAndStatus()
    {
        var catalog = NewCatalog();

        foreach (var domain in GlobalSearchDomains.Unified)
        {
            var result = catalog.GetCriteriaFor(domain);
            result.Should().Contain(
                c => c.MetadataKind == SearchCriterionMetadataKind.FullText,
                $"domain '{domain}' MUST publish a full-text criterion");
            result.Should().Contain(
                c => c.MetadataKind == SearchCriterionMetadataKind.DateRange,
                $"domain '{domain}' MUST publish a date-range criterion");
            result.Should().Contain(
                c => c.MetadataKind == SearchCriterionMetadataKind.Status,
                $"domain '{domain}' MUST publish a status criterion");
        }
    }

    /// <summary>Documents carry the document-metadata kind (DocumentTypeCode).</summary>
    [Fact]
    public void GetCriteriaFor_Documents_CarriesDocumentMetadata()
    {
        var catalog = NewCatalog();

        var result = catalog.GetCriteriaFor(GlobalSearchDomains.Documents);

        result.Should().Contain(
            c => c.MetadataKind == SearchCriterionMetadataKind.DocumentMetadata,
            "documents carry the DocumentTypeCode (CF 03.02)");
    }

    /// <summary>Contributors / payers carry the payer-metadata kind.</summary>
    [Fact]
    public void GetCriteriaFor_Contributors_CarriesPayerMetadata()
    {
        var catalog = NewCatalog();

        var result = catalog.GetCriteriaFor(GlobalSearchDomains.Contributors);

        result.Should().Contain(
            c => c.MetadataKind == SearchCriterionMetadataKind.PayerMetadata,
            "contributors carry the ContributorCode (CF 03.02)");
    }

    /// <summary>Insured persons carry the insured-metadata kind (Idnp).</summary>
    [Fact]
    public void GetCriteriaFor_InsuredPersons_CarriesInsuredMetadata()
    {
        var catalog = NewCatalog();

        var result = catalog.GetCriteriaFor(GlobalSearchDomains.InsuredPersons);

        result.Should().Contain(
            c => c.MetadataKind == SearchCriterionMetadataKind.InsuredMetadata,
            "insured persons carry the Idnp (CF 03.02)");
    }

    /// <summary>
    /// Every Classifier-typed criterion published by the catalogue MUST
    /// also carry the <see cref="SearchCriterionMetadataKind.ClassifierValue"/>
    /// kind AND a non-empty <see cref="SearchCriterionDescriptor.LookupSource"/>
    /// — the UI cannot render the lookup widget otherwise.
    /// </summary>
    [Fact]
    public void GetCriteriaFor_ClassifierCriteria_AdvertiseClassifierValueKindAndLookupSource()
    {
        var catalog = NewCatalog();

        foreach (var domain in GlobalSearchDomains.Unified)
        {
            var classifierEntries = catalog.GetCriteriaFor(domain)
                .Where(c => c.DataType == SearchDataType.Classifier)
                .ToList();
            foreach (var entry in classifierEntries)
            {
                entry.MetadataKind.Should().Be(
                    SearchCriterionMetadataKind.ClassifierValue,
                    $"domain '{domain}' classifier '{entry.Field}' should declare ClassifierValue kind");
                entry.LookupSource.Should().NotBeNullOrEmpty(
                    $"domain '{domain}' classifier '{entry.Field}' must declare a LookupSource");
            }
        }
    }
}
