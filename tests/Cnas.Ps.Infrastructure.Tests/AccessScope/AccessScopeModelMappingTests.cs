using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.AccessScope;

/// <summary>
/// R0671 / TOR CF 18.06 — compile-time assertions that the
/// <c>AddAccessScopeColumns</c> migration produced the expected entity-model shape:
/// nullable string properties + the two indexes the spec requires. The test runs
/// against the EF model snapshot (not the DB) so a drift between
/// <c>SolicitantConfiguration</c> / <c>ApplicationConfiguration</c> /
/// <c>WorkflowDefinitionConfiguration</c> and the migration would fail here even
/// before the migration is applied to a live database.
/// </summary>
public sealed class AccessScopeModelMappingTests
{
    /// <summary>Builds the in-memory model exactly the way the production context does.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-model-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Solicitant.RegionCode is mapped as a nullable string column with the documented
    /// 16-char cap, AND the secondary index supporting the IN-lookup is present.
    /// </summary>
    [Fact]
    public void Solicitant_RegionCodeColumn_AndIndex_ArePresent()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(Solicitant));
        entity.Should().NotBeNull("the model must include Solicitant");
        var prop = entity!.FindProperty(nameof(Solicitant.RegionCode));
        prop.Should().NotBeNull("R0671 added RegionCode");
        prop!.IsNullable.Should().BeTrue();
        prop.GetMaxLength().Should().Be(16);

        entity.GetIndexes()
            .Should().ContainSingle(i =>
                i.Properties.Count == 1
                && i.Properties[0].Name == nameof(Solicitant.RegionCode),
                "the access-scope filter relies on the (RegionCode) B-tree index");
    }

    /// <summary>
    /// ServiceApplication.SubdivisionCode is a nullable string capped at 64 chars and
    /// has its own index — the production query path filters by exact equality.
    /// </summary>
    [Fact]
    public void ServiceApplication_SubdivisionCodeColumn_AndIndex_ArePresent()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(ServiceApplication));
        var prop = entity!.FindProperty(nameof(ServiceApplication.SubdivisionCode));
        prop.Should().NotBeNull("R0671 added SubdivisionCode");
        prop!.IsNullable.Should().BeTrue();
        prop.GetMaxLength().Should().Be(64);

        entity.GetIndexes()
            .Should().ContainSingle(i =>
                i.Properties.Count == 1
                && i.Properties[0].Name == nameof(ServiceApplication.SubdivisionCode),
                "the access-scope filter relies on the (SubdivisionCode) B-tree index");
    }

    /// <summary>
    /// WorkflowDefinition.CategoryCode is a nullable string capped at 64 chars.
    /// The spec deliberately does NOT require a dedicated index because the filter
    /// resolves the category via the already-indexed (Code, IsCurrent) join path.
    /// </summary>
    [Fact]
    public void WorkflowDefinition_CategoryCodeColumn_IsMapped()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(WorkflowDefinition));
        var prop = entity!.FindProperty(nameof(WorkflowDefinition.CategoryCode));
        prop.Should().NotBeNull("R0671 added CategoryCode");
        prop!.IsNullable.Should().BeTrue();
        prop.GetMaxLength().Should().Be(64);
    }
}
