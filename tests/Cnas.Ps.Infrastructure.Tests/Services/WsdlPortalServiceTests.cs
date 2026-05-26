using System.Xml.Linq;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R2164 / TOR §15.4 INT 005 — TDD coverage for <see cref="WsdlPortalService"/>. Asserts
/// the listing surface, the per-controller WSDL generation contract, and the well-
/// formed XML invariant required by downstream SOAP-stub tooling.
/// </summary>
public sealed class WsdlPortalServiceTests
{
    /// <summary>Builds the SUT scanning the Cnas.Ps.Api assembly for controllers.</summary>
    private static WsdlPortalService BuildSut() =>
        new(typeof(Cnas.Ps.Api.Controllers.HealthDatabaseController).Assembly);

    [Fact]
    public async Task ListAsync_ReturnsAtLeastOneController()
    {
        var svc = BuildSut();

        var result = await svc.ListAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        result.Value.Should().OnlyContain(c => !c.ControllerName.EndsWith("Controller", StringComparison.Ordinal));
        result.Value.Should().OnlyContain(c => c.WsdlUrl.EndsWith(".wsdl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetForControllerAsync_KnownController_ReturnsWellFormedWsdl()
    {
        var svc = BuildSut();

        var result = await svc.GetForControllerAsync("HealthDatabase", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var descriptor = result.Value!;
        descriptor.ControllerName.Should().Be("HealthDatabase");
        descriptor.WsdlXml.Should().NotBeNullOrEmpty();

        // The cardinal acceptance gate: the body must parse as XML. SOAP-stub tooling
        // (wsimport, svcutil) fails fast on malformed WSDL so we lock the invariant
        // explicitly via XDocument.Parse.
        var doc = XDocument.Parse(descriptor.WsdlXml);
        doc.Root.Should().NotBeNull();
        doc.Root!.Name.LocalName.Should().Be("definitions");
        descriptor.Operations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetForControllerAsync_KnownController_IsCaseInsensitive()
    {
        var svc = BuildSut();

        var result = await svc.GetForControllerAsync("healthdatabase", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ControllerName.Should().Be("HealthDatabase");
    }

    [Fact]
    public async Task GetForControllerAsync_UnknownController_ReturnsNotFound()
    {
        var svc = BuildSut();

        var result = await svc.GetForControllerAsync("NoSuchController", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
