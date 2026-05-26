using NetArchTest.Rules;

namespace Cnas.Ps.Architecture.Tests;

/// <summary>
/// Cross-layer boundary rules that go beyond the assembly-dependency checks in
/// <see cref="LayerBoundaryTests"/>: they reach into specific namespaces and
/// shapes (sealed-ness, EF Core leakage) to enforce CLAUDE.md §1.1 + §2.3.
/// </summary>
public class BoundaryRulesTests
{
    [Fact]
    public void Controllers_Do_Not_Depend_On_DbContext()
    {
        // Controllers must orchestrate via services. Reaching into EF Core directly would
        // re-introduce a presentation→infrastructure dependency that violates CLAUDE.md §1.1.
        var apiAssembly = typeof(Cnas.Ps.Api.Controllers.ApplicationsController).Assembly;

        var result = Types.InAssembly(apiAssembly)
            .That()
            .ResideInNamespace("Cnas.Ps.Api.Controllers")
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "controllers in Cnas.Ps.Api.Controllers must NOT depend on Microsoft.EntityFrameworkCore — " +
            "they must call services instead. Offenders: {0}",
            FormatTypes(result.FailingTypeNames));
    }

    [Fact]
    public void Application_Validators_Are_Sealed_And_Internal_Or_Public()
    {
        // Validators are leaf classes — sealing them prevents accidental inheritance that
        // would change validation behaviour at runtime.
        var applicationAssembly = typeof(Cnas.Ps.Application.ApplicationAssemblyMarker).Assembly;

        var result = Types.InAssembly(applicationAssembly)
            .That()
            .ResideInNamespace("Cnas.Ps.Application.Validators")
            .And().AreClasses()
            .And().AreNotAbstract()
            .Should()
            .BeSealed()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "validators in Cnas.Ps.Application.Validators must be sealed (no subclassing). " +
            "Offenders: {0}",
            FormatTypes(result.FailingTypeNames));
    }

    [Fact]
    public void Service_Implementations_Are_Sealed()
    {
        // Concrete service classes in the Infrastructure layer must be sealed. Records are
        // exempt because record types have generated equality and Roslyn allows them to be
        // inherited from when not sealed — but they don't appear here yet.
        var infrastructureAssembly = typeof(Cnas.Ps.Infrastructure.InfrastructureServiceCollectionExtensions).Assembly;

        var result = Types.InAssembly(infrastructureAssembly)
            .That()
            .ResideInNamespace("Cnas.Ps.Infrastructure.Services")
            .And().AreClasses()
            .And().AreNotAbstract()
            .Should()
            .BeSealed()
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "service implementations in Cnas.Ps.Infrastructure.Services must be sealed " +
            "to prevent unintended inheritance. Offenders: {0}",
            FormatTypes(result.FailingTypeNames));
    }

    /// <summary>Renders failing-type names for diagnostic output.</summary>
    private static string FormatTypes(IEnumerable<string>? names)
        => names is null ? "<none>" : string.Join(", ", names);
}
