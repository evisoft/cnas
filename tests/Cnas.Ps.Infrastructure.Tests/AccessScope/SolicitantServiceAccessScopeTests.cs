using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.AccessScope;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Qbe;
using Cnas.Ps.Infrastructure.QueryBudget;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.AccessScope;

/// <summary>
/// R0671 / TOR CF 18.06 — verifies the wiring contract: the
/// <see cref="SolicitantService"/> applies the
/// <see cref="Cnas.Ps.Application.AccessScope.IAccessScopeFilter"/> BEFORE the
/// query-budget gate so the budget evaluates the SCOPED row count.
/// </summary>
public sealed class SolicitantServiceAccessScopeTests
{
    /// <summary>Region allow-list for the scoped budget test (CHIS rows only).</summary>
    private static readonly string[] ChisOnly = ["CHIS"];

    /// <summary>Region allow-list for the NULL-tolerance test (matches no real RegionCode).</summary>
    private static readonly string[] ZzzOnly = ["ZZZ"];

    /// <summary>Empty array shared between scope-builder calls so CA1825 stays quiet.</summary>
    private static readonly string[] EmptyAxis = Array.Empty<string>();

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-svc-scope-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Builds the SUT against a real budget service + InMemory DB + caller stub whose
    /// AccessScope returns the supplied envelope.
    /// </summary>
    private static (SolicitantService Svc, CnasDbContext Db) Build(
        IAccessScope scope, int? budgetOverride = null)
    {
        var db = CreateContext();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        IQueryBudgetPolicy policy = budgetOverride is { } b
            ? new SingleBudgetPolicy(b)
            : new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance);
        var budget = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        var qbeConverter = new QbeToLinqConverter(new QbeRegistrySchemaProvider());
        var suggestions = new Cnas.Ps.Infrastructure.Search.SearchSuggestionService(new QbeRegistrySchemaProvider());
        var accessFilter = new AccessScopeFilter();
        var caller = Substitute.For<ICallerContext>();
        caller.AccessScope.Returns(scope);
        // R0623 — guard + clock are unused by the access-scope tests but the
        // constructor requires them; substitute a no-op pair so the harness stays
        // focused on the access-scope behaviour under test.
        var referenceGuard = Substitute.For<Cnas.Ps.Application.Solicitants.ISolicitantReferenceGuard>();
        var clock = Substitute.For<Cnas.Ps.Core.Common.ICnasTimeProvider>();
        var svc = new SolicitantService(db, sqids, budget, qbeConverter, suggestions, accessFilter, caller, referenceGuard, clock);
        return (svc, db);
    }

    /// <summary>
    /// Seeds a mix of region-tagged + NULL-region rows so the predicate's NULL-tolerance
    /// can be observed in the same test.
    /// </summary>
    private static async Task SeedAsync(CnasDbContext db)
    {
        for (int i = 0; i < 20; i++)
        {
            db.Solicitants.Add(new Solicitant
            {
                NationalId = $"2{i:D12}",
                NationalIdHash = $"h-{i}",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = $"Person {i:D5}",
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
                RegionCode = i < 5 ? "CHIS" : i < 10 ? "BLT" : (i < 15 ? "BAL" : null),
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The access-scope filter runs BEFORE the budget gate — the budget verdict sees
    /// the SCOPED row count (5 CHIS rows + 5 NULL rows = 10), not the unscoped 20.
    /// This is the load-bearing security property: budget cannot be bypassed by
    /// being unscoped via accidental misconfiguration.
    /// </summary>
    [Fact]
    public async Task ListAsync_ScopedCallerSeesScopedCount_BeforeBudgetGate()
    {
        // Scope = CHIS only. 5 CHIS rows + 5 NULL rows visible = 10 total scoped.
        var scope = new RolesBasedAccessScope(ChisOnly, EmptyAxis, EmptyAxis, EmptyAxis);
        // Budget = 15: would refuse the unscoped 20 but allow the scoped 10.
        var (svc, db) = Build(scope, budgetOverride: 15);
        await SeedAsync(db);

        var result = await svc.ListAsync(new SolicitantListQueryInput());

        result.IsSuccess.Should().BeTrue();
        svc.LastBudgetVerdict.Should().NotBeNull();
        svc.LastBudgetVerdict!.EstimatedRowCount.Should().Be(10);
        result.Value.TotalCount.Should().Be(10);
    }

    /// <summary>
    /// An unscoped caller sees ALL rows — the filter is a no-op when AccessScope is
    /// unscoped, and the budget evaluates the full 20-row count.
    /// </summary>
    [Fact]
    public async Task ListAsync_UnscopedCaller_SeesAllRows()
    {
        var (svc, db) = Build(RolesBasedAccessScope.Unscoped);
        await SeedAsync(db);

        var result = await svc.ListAsync(new SolicitantListQueryInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(20);
    }

    /// <summary>
    /// Rows whose RegionCode is NULL are visible to every scoped caller (the
    /// documented NULL-data semantics). This test exercises the property end-to-end
    /// through the service so the design choice is asserted at the boundary the
    /// controller actually consumes.
    /// </summary>
    [Fact]
    public async Task ListAsync_NullRegionRows_AreVisibleToScopedCallers()
    {
        // Scope = only "ZZZ" — nothing matches RegionCode, but the 5 NULL rows must
        // still surface.
        var scope = new RolesBasedAccessScope(ZzzOnly, EmptyAxis, EmptyAxis, EmptyAxis);
        var (svc, db) = Build(scope);
        await SeedAsync(db);

        var result = await svc.ListAsync(new SolicitantListQueryInput { PageSize = 100 });

        result.IsSuccess.Should().BeTrue();
        // Only the 5 NULL-region rows survive — none of the CHIS/BLT/BAL match "ZZZ".
        result.Value.TotalCount.Should().Be(5);
    }

    /// <summary>Test stub that returns a single-budget policy regardless of registry —
    /// mirrors the pattern from <c>SolicitantServiceQueryBudgetTests</c>.</summary>
    private sealed class SingleBudgetPolicy(int budget) : IQueryBudgetPolicy
    {
        /// <inheritdoc />
        public QueryBudgetPolicy GetForRegistry(string registry) =>
            new(registry, budget, Array.Empty<RefinementHintRule>());
    }
}
