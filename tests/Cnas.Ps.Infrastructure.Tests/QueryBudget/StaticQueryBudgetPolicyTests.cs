using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Infrastructure.QueryBudget;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.QueryBudget;

/// <summary>
/// R0167 / TOR CF 01.06 — tests for the production <see cref="StaticQueryBudgetPolicy"/>
/// registry. The static map must contain entries for every registry the design document
/// names; unknown registries must fall back to the default budget without throwing.
/// </summary>
public class StaticQueryBudgetPolicyTests
{
    /// <summary>Builds the system-under-test with a no-op logger.</summary>
    private static StaticQueryBudgetPolicy CreatePolicy() =>
        new(NullLogger<StaticQueryBudgetPolicy>.Instance);

    [Theory]
    [InlineData(QueryBudgetRegistries.Solicitant)]
    [InlineData(QueryBudgetRegistries.Cerere)]
    [InlineData(QueryBudgetRegistries.WorkflowTask)]
    [InlineData(QueryBudgetRegistries.Decision)]
    [InlineData(QueryBudgetRegistries.Document)]
    [InlineData(QueryBudgetRegistries.AuditLog)]
    public void GetForRegistry_KnownRegistries_ReturnsBudgetPolicy(string registry)
    {
        var resolver = CreatePolicy();

        var policy = resolver.GetForRegistry(registry);

        policy.Registry.Should().Be(registry);
        policy.Budget.Should().BeGreaterThan(0);
        policy.Rules.Should().NotBeEmpty();
    }

    [Fact]
    public void GetForRegistry_AuditLog_HasTighterBudget()
    {
        var resolver = CreatePolicy();

        var policy = resolver.GetForRegistry(QueryBudgetRegistries.AuditLog);

        policy.Budget.Should().Be(1000);
    }

    [Fact]
    public void GetForRegistry_Solicitant_HasDefault5000Budget()
    {
        var resolver = CreatePolicy();

        var policy = resolver.GetForRegistry(QueryBudgetRegistries.Solicitant);

        policy.Budget.Should().Be(QueryBudgetPolicy.DefaultBudget);
    }

    [Fact]
    public void GetForRegistry_UnknownRegistry_ReturnsDefaultBudgetWithoutRules()
    {
        var resolver = CreatePolicy();

        var policy = resolver.GetForRegistry("NotARealRegistry");

        policy.Registry.Should().Be("NotARealRegistry");
        policy.Budget.Should().Be(QueryBudgetPolicy.DefaultBudget);
        policy.Rules.Should().BeEmpty();
    }

    [Fact]
    public void GetForRegistry_NullRegistry_ReturnsDefaultPolicy()
    {
        var resolver = CreatePolicy();

        var policy = resolver.GetForRegistry(null!);

        policy.Budget.Should().Be(QueryBudgetPolicy.DefaultBudget);
        policy.Rules.Should().BeEmpty();
    }
}
