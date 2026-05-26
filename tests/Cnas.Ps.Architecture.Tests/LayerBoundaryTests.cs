using NetArchTest.Rules;

namespace Cnas.Ps.Architecture.Tests;

/// <summary>
/// Architecture tests for SI PS. Layer boundaries are encoded so that violations are caught at
/// build time per CLAUDE.md §1.1 (Day-1 Foundation). Each new violation must be either fixed or
/// grandfathered explicitly in the ratchet list.
/// </summary>
public class LayerBoundaryTests
{
    [Fact]
    public void Core_HasNoOutboundDependenciesOnOtherLayers()
    {
        var result = Types.InAssembly(typeof(Cnas.Ps.Core.Common.Result).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Cnas.Ps.Application",
                "Cnas.Ps.Infrastructure",
                "Cnas.Ps.Api",
                "Cnas.Ps.Web")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrApi()
    {
        var result = Types.InAssembly(typeof(Cnas.Ps.Application.ApplicationAssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny("Cnas.Ps.Infrastructure", "Cnas.Ps.Api", "Cnas.Ps.Web")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnApi()
    {
        var result = Types.InAssembly(typeof(Cnas.Ps.Infrastructure.InfrastructureServiceCollectionExtensions).Assembly)
            .Should()
            .NotHaveDependencyOnAny("Cnas.Ps.Api", "Cnas.Ps.Web")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Contracts_HasNoOutboundDependencies()
    {
        var result = Types.InAssembly(typeof(Cnas.Ps.Contracts.PageRequest).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Cnas.Ps.Core",
                "Cnas.Ps.Application",
                "Cnas.Ps.Infrastructure",
                "Cnas.Ps.Api",
                "Cnas.Ps.Web",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }
}
