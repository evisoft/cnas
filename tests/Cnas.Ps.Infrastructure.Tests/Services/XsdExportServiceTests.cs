using System.Xml.Linq;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R2135 / TOR §15.2 ARH 026 — TDD coverage for <see cref="XsdExportService"/>.
/// Asserts the listing surface, per-DTO XSD generation, the well-formed XML
/// invariant, and the allow-list deny-by-default behaviour required by
/// CLAUDE.md §5.4.
/// </summary>
public sealed class XsdExportServiceTests
{
    /// <summary>Builds the SUT with the default Contracts allow-list.</summary>
    private static XsdExportService BuildSut() => new();

    [Fact]
    public async Task ListAsync_ReturnsCuratedAllowList()
    {
        var svc = BuildSut();

        var result = await svc.ListAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        // Starter allow-list must surface the five representative DTOs.
        result.Value.Should().Contain("ApplicationOutput");
        result.Value.Should().Contain("NotificationOutput");
        result.Value.Should().Contain("ClaimDto");
        // Sorted alphabetically so the listing is stable across invocations.
        result.Value.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("ApplicationOutput")]
    [InlineData("NotificationOutput")]
    [InlineData("ClaimDto")]
    [InlineData("DecisionListItemDto")]
    [InlineData("DocumentListItemDto")]
    public async Task ExportAsync_KnownDto_ReturnsWellFormedXsdWithTargetElement(string dtoName)
    {
        var svc = BuildSut();

        var result = await svc.ExportAsync(dtoName, CancellationToken.None);

        result.IsSuccess.Should().BeTrue($"DTO '{dtoName}' must be in the allow-list");
        var xsd = result.Value;
        xsd.Should().NotBeNullOrEmpty();

        // Cardinal acceptance gate: the body MUST parse as XML so downstream
        // schema-registry tooling can ingest it. SOAP/XSD stub tooling fails
        // fast on malformed schemas; we lock the invariant here.
        var doc = XDocument.Parse(xsd);
        doc.Root.Should().NotBeNull();
        doc.Root!.Name.LocalName.Should().Be("schema");
        doc.Root.Name.NamespaceName.Should().Be("http://www.w3.org/2001/XMLSchema");

        // Target type must surface as a top-level <xs:element name="..."> entry.
        var elements = doc.Root.Elements()
            .Where(e => e.Name.LocalName == "element")
            .ToList();
        elements.Should().Contain(e => (string?)e.Attribute("name") == dtoName,
            $"top-level <xs:element name='{dtoName}'> must be present");
    }

    [Fact]
    public async Task ExportAsync_UnknownDto_ReturnsNotFound()
    {
        var svc = BuildSut();

        var result = await svc.ExportAsync("NoSuchDto", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task ExportAsync_EmptyName_ReturnsValidationFailed()
    {
        var svc = BuildSut();

        var result = await svc.ExportAsync("   ", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task ExportAsync_KnownDto_IsCaseInsensitive()
    {
        var svc = BuildSut();

        var result = await svc.ExportAsync("applicationoutput", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var doc = XDocument.Parse(result.Value);
        doc.Root!.Elements()
            .Where(e => e.Name.LocalName == "element")
            .Should().Contain(e => (string?)e.Attribute("name") == "ApplicationOutput");
    }

    [Fact]
    public async Task ExportAsync_ClaimDto_TypesIdAsString()
    {
        // CLAUDE.md RULE 3: every external `Id` field must be a Sqid-encoded string.
        // The XSD MUST therefore type the `Id` element as xs:string, not xs:long.
        var svc = BuildSut();

        var result = await svc.ExportAsync("ClaimDto", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var doc = XDocument.Parse(result.Value);
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        var complexType = doc.Descendants(xs + "complexType")
            .First(t => (string?)t.Attribute("name") == "ClaimDtoType");
        var idElement = complexType.Descendants(xs + "element")
            .FirstOrDefault(e => (string?)e.Attribute("name") == "Id");
        idElement.Should().NotBeNull();
        ((string?)idElement!.Attribute("type")).Should().EndWith(":string");
    }
}
