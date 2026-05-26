using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Infrastructure.Qbe;

namespace Cnas.Ps.Infrastructure.Tests.Qbe;

/// <summary>
/// R0822 — registry-schema + budget-registry registration tests for the
/// Declarations explorer. Locks down that adding a new registry requires
/// matching entries in both <see cref="QbeRegistrySchemaProvider"/> and
/// <see cref="QueryBudgetRegistries"/>.
/// </summary>
public sealed class DeclarationRegistrySchemaTests
{
    /// <summary>
    /// R0822 — the QBE schema provider returns a populated schema for the
    /// <c>"Declaration"</c> registry code (case-sensitive lookup mirrors
    /// production behaviour).
    /// </summary>
    [Fact]
    public void GetForRegistry_Declaration_ReturnsSchema()
    {
        var provider = new QbeRegistrySchemaProvider();

        var schema = provider.GetForRegistry(QueryBudgetRegistries.Declaration);

        schema.Should().NotBeNull();
        schema!.RegistryCode.Should().Be("Declaration");
        schema.Fields.Should().Contain(f => f.FieldName == "Kind");
        schema.Fields.Should().Contain(f => f.FieldName == "ContributorId");
        schema.Fields.Should().Contain(f => f.FieldName == "ReportingMonth");
        schema.Fields.Should().Contain(f => f.FieldName == "FiledAtUtc");
        schema.Fields.Should().Contain(f => f.FieldName == "Status");
        schema.Fields.Should().Contain(f => f.FieldName == "HasScannedCopy");
    }

    /// <summary>
    /// R0822 — the canonical query-budget registry allow-list carries the
    /// <c>Declaration</c> entry so the explorer endpoint passes the
    /// <see cref="QueryBudgetRegistries.IsKnown"/> guard.
    /// </summary>
    [Fact]
    public void QueryBudgetRegistries_Declaration_IsKnown()
    {
        QueryBudgetRegistries.IsKnown(QueryBudgetRegistries.Declaration).Should().BeTrue();
        QueryBudgetRegistries.Declaration.Should().Be("Declaration");
    }
}
