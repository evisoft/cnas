using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Qbe;
using Cnas.Ps.Infrastructure.QueryBudget;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.QueryBudget;

/// <summary>
/// R0167 — service-level vertical-slice tests for the Solicitant registry list call.
/// Asserts the budget guard properly refuses too-broad calls while allowing well-
/// filtered ones.
/// </summary>
public class SolicitantServiceQueryBudgetTests
{
    /// <summary>Fresh InMemory <see cref="CnasDbContext"/> with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-solicitant-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Builds the system-under-test against a real budget service + InMemory DB.</summary>
    private static (SolicitantService Svc, CnasDbContext Db) Build(int? budgetOverride = null)
    {
        var db = CreateContext();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        IQueryBudgetPolicy policy = budgetOverride is { } b
            ? new SingleBudgetPolicy(b)
            : new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance);
        var budget = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        var qbeConverter = new QbeToLinqConverter(new QbeRegistrySchemaProvider());
        // R0525 — suggestion service is required dependency in this batch; use the
        // production implementation against the same schema provider so the budget
        // verdict's row count drives both the budget guard and the suggestion path
        // identically.
        var suggestions = new Cnas.Ps.Infrastructure.Search.SearchSuggestionService(new QbeRegistrySchemaProvider());
        // R0671 — supply the real access-scope filter with an unscoped caller so the
        // pre-existing budget-gate tests continue to assert the unscoped pipeline
        // behaviour. Scope-aware behaviour gets its own dedicated test file.
        var accessFilter = new Cnas.Ps.Infrastructure.AccessScope.AccessScopeFilter();
        var caller = Substitute.For<ICallerContext>();
        caller.AccessScope.Returns(Cnas.Ps.Infrastructure.AccessScope.RolesBasedAccessScope.Unscoped);
        // R0623 — guard + clock are unused by the budget-gate tests but the
        // constructor requires them; substitute a no-op pair.
        var referenceGuard = Substitute.For<Cnas.Ps.Application.Solicitants.ISolicitantReferenceGuard>();
        var clock = Substitute.For<Cnas.Ps.Core.Common.ICnasTimeProvider>();
        var svc = new SolicitantService(db, sqids, budget, qbeConverter, suggestions, accessFilter, caller, referenceGuard, clock);
        return (svc, db);
    }

    /// <summary>Seeds <paramref name="rowCount"/> active solicitants for the registry budget tests.</summary>
    private static async Task SeedAsync(CnasDbContext db, int rowCount)
    {
        for (int i = 0; i < rowCount; i++)
        {
            db.Solicitants.Add(new Solicitant
            {
                NationalId = $"2{i:D12}",
                NationalIdHash = $"h-{i}",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = $"Person {i:D5}",
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();
    }

    // ───────── Vertical slice tests ─────────

    [Fact]
    public async Task ListAsync_NoFilters_AndDatasetExceedsBudget_ReturnsQueryTooBroad()
    {
        // Tight budget (10) + 50 rows + empty filter set → must fail with QUERY_TOO_BROAD
        // and expose the verdict on the service's LastBudgetVerdict slot for the
        // controller to harvest.
        var (svc, db) = Build(budgetOverride: 10);
        await SeedAsync(db, 50);

        var result = await svc.ListAsync(new SolicitantListQueryInput());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.QueryTooBroad);
        svc.LastBudgetVerdict.Should().NotBeNull();
        svc.LastBudgetVerdict!.Allowed.Should().BeFalse();
        svc.LastBudgetVerdict.EstimatedRowCount.Should().Be(50);
        svc.LastBudgetVerdict.Budget.Should().Be(10);
    }

    [Fact]
    public async Task ListAsync_WithinBudget_ReturnsPagedResultWithSqidIds()
    {
        var (svc, db) = Build();
        await SeedAsync(db, 3);

        var result = await svc.ListAsync(new SolicitantListQueryInput());

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(3);
        result.Value.Items.Should().OnlyContain(i => i.Id.StartsWith("SQID-"));
        // The verdict slot still holds the verdict (allowed=true) for diagnostic use.
        svc.LastBudgetVerdict.Should().NotBeNull();
        svc.LastBudgetVerdict!.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ListAsync_FreshCall_ResetsLastBudgetVerdictSlot()
    {
        // A second call against a tight budget should produce a verdict that is NOT
        // stale from a previous in-memory accumulation — explicitly tests the
        // "reset on entry" contract.
        var (svc, db) = Build(budgetOverride: 5);
        await SeedAsync(db, 20);

        // First call → too broad → verdict populated.
        var first = await svc.ListAsync(new SolicitantListQueryInput());
        first.IsFailure.Should().BeTrue();
        svc.LastBudgetVerdict.Should().NotBeNull();

        // Add a date range that the verdict will still reject (because the static
        // override only has a budget, no rules consume the date) so the verdict slot
        // is re-populated rather than carried over.
        var second = await svc.ListAsync(new SolicitantListQueryInput(
            CreatedFromUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        second.IsFailure.Should().BeTrue();
        svc.LastBudgetVerdict.Should().NotBeNull();
        svc.LastBudgetVerdict!.EstimatedRowCount.Should().Be(20);
    }

    /// <summary>Test stub that returns a single-budget policy regardless of registry.</summary>
    private sealed class SingleBudgetPolicy(int budget) : IQueryBudgetPolicy
    {
        public QueryBudgetPolicy GetForRegistry(string registry) =>
            new(registry, budget, Array.Empty<RefinementHintRule>());
    }
}
