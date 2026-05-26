using System.Linq;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Contracts;

namespace Cnas.Ps.Application.Tests.Search;

/// <summary>
/// R0501 / TOR CF 01.04 — unit tests for <see cref="ISearchCriteriaCatalog"/>
/// implementations. Verifies the per-domain criteria registry exposes the
/// expected field set for every canonical domain.
/// </summary>
public sealed class SearchCriteriaCatalogTests
{
    /// <summary>Expected core criteria for the applications domain.</summary>
    private static readonly string[] ExpectedApplicationsFields =
        new[] { "code", "status", "dossierNumber", "applicantName", "dateFrom", "dateTo" };

    /// <summary>Expected core criteria for the contributors domain.</summary>
    private static readonly string[] ExpectedContributorsFields =
        new[] { "idno", "name", "status" };

    /// <summary>Expected core criteria for the insured-persons domain.</summary>
    private static readonly string[] ExpectedInsuredFields =
        new[] { "idnp", "name", "employer" };

    /// <summary>Expected core criteria for the tasks domain.</summary>
    private static readonly string[] ExpectedTasksFields =
        new[] { "status", "assignee" };

    /// <summary>Returns the production registry under test.</summary>
    /// <returns>The wired catalogue.</returns>
    private static ISearchCriteriaCatalog NewCatalog() => new StaticSearchCriteriaCatalog();

    /// <summary>
    /// Unknown domain yields an empty list — never throws. The endpoint
    /// translates the empty list into 404 at the controller boundary.
    /// </summary>
    [Fact]
    public void GetCriteriaFor_UnknownDomain_ReturnsEmpty()
    {
        var catalog = NewCatalog();

        var result = catalog.GetCriteriaFor("nope-not-a-domain");

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Applications domain exposes at least: code (reference number),
    /// status, dossierNumber, applicantName, dateFrom + dateTo (date range).
    /// </summary>
    [Fact]
    public void GetCriteriaFor_Applications_PublishesCoreCriteria()
    {
        var catalog = NewCatalog();

        var result = catalog.GetCriteriaFor(GlobalSearchDomains.Applications);

        result.Should().NotBeEmpty();
        result.Select(c => c.Field).Should().Contain(ExpectedApplicationsFields);
    }

    /// <summary>Contributors domain exposes idno, name, status criteria.</summary>
    [Fact]
    public void GetCriteriaFor_Contributors_PublishesCoreCriteria()
    {
        var catalog = NewCatalog();

        var result = catalog.GetCriteriaFor(GlobalSearchDomains.Contributors);

        result.Select(c => c.Field).Should().Contain(ExpectedContributorsFields);
    }

    /// <summary>Insured-persons domain exposes idnp, name, employer criteria.</summary>
    [Fact]
    public void GetCriteriaFor_InsuredPersons_PublishesCoreCriteria()
    {
        var catalog = NewCatalog();

        var result = catalog.GetCriteriaFor(GlobalSearchDomains.InsuredPersons);

        result.Select(c => c.Field).Should().Contain(ExpectedInsuredFields);
    }

    /// <summary>Tasks domain exposes status, assignee criteria.</summary>
    [Fact]
    public void GetCriteriaFor_Tasks_PublishesCoreCriteria()
    {
        var catalog = NewCatalog();

        var result = catalog.GetCriteriaFor(GlobalSearchDomains.Tasks);

        result.Select(c => c.Field).Should().Contain(ExpectedTasksFields);
    }

    /// <summary>
    /// Every criterion descriptor MUST have at least one operator declared —
    /// an empty operator list would be a broken contract (the UI cannot bind).
    /// </summary>
    [Fact]
    public void GetCriteriaFor_EveryDomain_HasAtLeastOneOperatorPerCriterion()
    {
        var catalog = NewCatalog();

        foreach (var domain in GlobalSearchDomains.Unified)
        {
            var result = catalog.GetCriteriaFor(domain);
            result.Should().AllSatisfy(c =>
                c.Operators.Should().NotBeEmpty(
                    $"domain '{domain}' criterion '{c.Field}' must publish ≥1 operator"));
        }
    }

    /// <summary>
    /// Every Classifier-typed criterion MUST carry a non-empty LookupSource
    /// so the UI can fetch the allowed code list.
    /// </summary>
    [Fact]
    public void GetCriteriaFor_ClassifierFields_HaveLookupSource()
    {
        var catalog = NewCatalog();

        foreach (var domain in GlobalSearchDomains.Unified)
        {
            var result = catalog.GetCriteriaFor(domain);
            foreach (var c in result.Where(c => c.DataType == SearchDataType.Classifier))
            {
                c.LookupSource.Should().NotBeNullOrEmpty(
                    $"domain '{domain}' classifier field '{c.Field}' must declare a LookupSource");
            }
        }
    }
}
