using System.Text;
using System.Xml.Linq;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Templates;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0133 / R0134 — Unit tests for <see cref="TemplatesAdminController"/>. The 401 /
/// 403 paths are locked end-to-end by the auth pipeline E2E journey; these tests
/// exercise the controller logic directly with NSubstitute doubles.
/// </summary>
public sealed class TemplatesAdminControllerTests
{
    private static ITemplateVariantService NewVariantSvc() => Substitute.For<ITemplateVariantService>();
    private static ITemplateCatalogPort NewCatalog() => Substitute.For<ITemplateCatalogPort>();
    private static ISqidService NewSqids() => Substitute.For<ISqidService>();

    private static TemplatesAdminController NewController(
        ITemplateVariantService variants,
        ITemplateCatalogPort catalog,
        ISqidService sqids)
        => new(variants, catalog, sqids);

    [Fact]
    public async Task ExportXml_Returns200_WithApplicationXmlBody()
    {
        // Arrange — substitute returns a well-formed XML document.
        var catalog = NewCatalog();
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TemplateCatalog>
              <Template code="t-1" defaultLanguage="ro">
                <Variant language="ro" subject="S" approved="true"><![CDATA[B]]></Variant>
              </Template>
            </TemplateCatalog>
            """;
        var bytes = Encoding.UTF8.GetBytes(xml);
        catalog.ExportXmlAsync(Arg.Any<CancellationToken>())
               .Returns(Result<byte[]>.Success(bytes));

        var controller = NewController(NewVariantSvc(), catalog, NewSqids());

        // Act
        var result = await controller.ExportXmlAsync(CancellationToken.None);

        // Assert — FileContentResult with application/xml content-type AND parseable body.
        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/xml");
        file.FileContents.Should().BeEquivalentTo(bytes);
        // Parseable — proves the controller hands back the original payload unmodified.
        var parsed = XDocument.Parse(Encoding.UTF8.GetString(file.FileContents));
        parsed.Root!.Name.LocalName.Should().Be("TemplateCatalog");
    }

    [Fact]
    public async Task ImportXml_NullFile_Returns400()
    {
        var controller = NewController(NewVariantSvc(), NewCatalog(), NewSqids());

        var result = await controller.ImportXmlAsync(null!, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
