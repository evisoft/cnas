using System.Diagnostics.Metrics;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.QueryBudget;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.QueryBudget;

/// <summary>
/// R0167 / TOR CF 01.06 / CF 03.07-08 — tests for <see cref="QueryBudgetService"/>.
/// Exercises the count-first guard, hint emission, ordering invariant, cancellation
/// passthrough, and metric increments. Uses EF Core InMemory backing because the
/// service relies only on <c>LongCountAsync</c>, which translates on every provider.
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> because the service emits on the
/// process-static <see cref="CnasMeter"/>; running concurrently with another meter-
/// aware test class would pollute the "exactly N increments" assertions.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class QueryBudgetServiceTests
{
    /// <summary>Fresh InMemory <see cref="CnasDbContext"/> with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-querybudget-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Seeds <paramref name="rowCount"/> Solicitants for the test scenarios.</summary>
    /// <param name="db">Target context.</param>
    /// <param name="rowCount">Row count to seed.</param>
    private static async Task SeedSolicitantsAsync(CnasDbContext db, int rowCount)
    {
        for (int i = 0; i < rowCount; i++)
        {
            db.Solicitants.Add(new Solicitant
            {
                NationalId = $"1000000000{i:D3}",
                NationalIdHash = $"hash-{i}",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = $"Person {i}",
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Builds the system-under-test with a real <see cref="StaticQueryBudgetPolicy"/>.</summary>
    private static QueryBudgetService CreateService()
    {
        var policy = new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance);
        return new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
    }

    // ───────── Case 1: small queryable, budget allows ─────────

    [Fact]
    public async Task EvaluateAsync_SmallQuery_ReturnsAllowedTrue_AndNoHints()
    {
        await using var db = CreateContext();
        await SeedSolicitantsAsync(db, 3);
        var svc = CreateService();

        var verdict = await svc.EvaluateAsync(
            QueryBudgetRegistries.Solicitant,
            db.Solicitants.Where(s => s.IsActive),
            new QueryFilterContext(),
            CancellationToken.None);

        verdict.Allowed.Should().BeTrue();
        verdict.EstimatedRowCount.Should().Be(3);
        verdict.Hints.Should().BeEmpty();
        verdict.Registry.Should().Be(QueryBudgetRegistries.Solicitant);
        verdict.Budget.Should().Be(5000);
    }

    // ───────── Case 2: large queryable, budget rejects ─────────

    [Fact]
    public async Task EvaluateAsync_OverBudget_ReturnsAllowedFalse_AndEstimatedCount()
    {
        await using var db = CreateContext();
        // Use a tighter budget by registering a one-off policy through the resolver.
        var policy = new TestPolicyResolver(QueryBudgetPolicyBuilder
            .For("TestRegistry")
            .WithBudget(100)
            .Require("Q", RefinementHintReasons.AddFreeTextFilter)
            .Build());
        var svc = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        await SeedSolicitantsAsync(db, 250);

        var verdict = await svc.EvaluateAsync(
            "TestRegistry",
            db.Solicitants.Where(s => s.IsActive),
            new QueryFilterContext(),
            CancellationToken.None);

        verdict.Allowed.Should().BeFalse();
        verdict.EstimatedRowCount.Should().Be(250);
        verdict.Budget.Should().Be(100);
    }

    // ───────── Case 3: hint ordering — Required before Suggested ─────────

    [Fact]
    public async Task EvaluateAsync_RejectedVerdict_OrdersRequiredHintsBeforeSuggested()
    {
        await using var db = CreateContext();
        var policy = new TestPolicyResolver(QueryBudgetPolicyBuilder
            .For("OrderingRegistry")
            .WithBudget(5)
            .Suggest("CreatedFromUtc", RefinementHintReasons.AddDateFilter)
            .Require("Q", RefinementHintReasons.AddFreeTextFilter)
            .Build());
        var svc = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        await SeedSolicitantsAsync(db, 20);

        var verdict = await svc.EvaluateAsync(
            "OrderingRegistry",
            db.Solicitants.Where(s => s.IsActive),
            new QueryFilterContext(),
            CancellationToken.None);

        verdict.Hints.Should().HaveCount(2);
        verdict.Hints[0].Severity.Should().Be(RefinementHintSeverity.Required);
        verdict.Hints[0].FieldName.Should().Be("Q");
        verdict.Hints[1].Severity.Should().Be(RefinementHintSeverity.Suggested);
        verdict.Hints[1].FieldName.Should().Be("CreatedFromUtc");
    }

    // ───────── Case 4: Required hint fires when caller omitted required field ─────────

    [Fact]
    public async Task EvaluateAsync_RequiredFieldOmitted_FiresRequiredHint()
    {
        await using var db = CreateContext();
        var policy = new TestPolicyResolver(QueryBudgetPolicyBuilder
            .For("R")
            .WithBudget(5)
            .Require("Q", RefinementHintReasons.AddFreeTextFilter)
            .Build());
        var svc = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        await SeedSolicitantsAsync(db, 20);

        var verdict = await svc.EvaluateAsync(
            "R",
            db.Solicitants.Where(s => s.IsActive),
            new QueryFilterContext(), // empty — Q is missing
            CancellationToken.None);

        verdict.Hints.Should().ContainSingle();
        verdict.Hints[0].Severity.Should().Be(RefinementHintSeverity.Required);
        verdict.Hints[0].FieldName.Should().Be("Q");
    }

    // ───────── Case 5: Suggested hint fires when caller omitted suggested field ─────────

    [Fact]
    public async Task EvaluateAsync_SuggestedFieldOmitted_FiresSuggestedHint()
    {
        await using var db = CreateContext();
        var policy = new TestPolicyResolver(QueryBudgetPolicyBuilder
            .For("R")
            .WithBudget(5)
            .Suggest("CreatedFromUtc", RefinementHintReasons.AddDateFilter)
            .Build());
        var svc = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        await SeedSolicitantsAsync(db, 20);

        var verdict = await svc.EvaluateAsync(
            "R",
            db.Solicitants.Where(s => s.IsActive),
            new QueryFilterContext(), // empty — CreatedFromUtc is missing
            CancellationToken.None);

        verdict.Hints.Should().ContainSingle();
        verdict.Hints[0].Severity.Should().Be(RefinementHintSeverity.Suggested);
        verdict.Hints[0].FieldName.Should().Be("CreatedFromUtc");
    }

    // ───────── Case 6: Hint suppression — provided filter suppresses its hint ─────────

    [Fact]
    public async Task EvaluateAsync_FieldProvided_SuppressesItsHint()
    {
        await using var db = CreateContext();
        var policy = new TestPolicyResolver(QueryBudgetPolicyBuilder
            .For("R")
            .WithBudget(5)
            .Require("Q", RefinementHintReasons.AddFreeTextFilter)
            .Suggest("CreatedFromUtc", RefinementHintReasons.AddDateFilter)
            .Build());
        var svc = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        await SeedSolicitantsAsync(db, 20);

        // Caller supplied Q — the Required hint for Q must NOT fire; only the
        // suggested CreatedFromUtc hint should remain.
        var ctx = new QueryFilterContext().With("Q", "Popescu");
        var verdict = await svc.EvaluateAsync(
            "R",
            db.Solicitants.Where(s => s.IsActive),
            ctx,
            CancellationToken.None);

        verdict.Hints.Should().ContainSingle();
        verdict.Hints[0].FieldName.Should().Be("CreatedFromUtc");
        verdict.Hints[0].Severity.Should().Be(RefinementHintSeverity.Suggested);
    }

    // ───────── Case 7 & 8: counter increments ─────────

    [Fact]
    public async Task EvaluateAsync_IncrementsEvaluatedCounter_PerCall()
    {
        using var capture = new MetricCapture("cnas.query.budget_evaluated");
        await using var db = CreateContext();
        await SeedSolicitantsAsync(db, 3);
        var svc = CreateService();

        await svc.EvaluateAsync(
            QueryBudgetRegistries.Solicitant,
            db.Solicitants.Where(s => s.IsActive),
            new QueryFilterContext(),
            CancellationToken.None);

        capture.TotalIncrement.Should().Be(1);
    }

    [Fact]
    public async Task EvaluateAsync_OverBudget_IncrementsRejectedCounter()
    {
        using var capture = new MetricCapture("cnas.query.budget_rejected");
        await using var db = CreateContext();
        var policy = new TestPolicyResolver(QueryBudgetPolicyBuilder
            .For("R")
            .WithBudget(5)
            .Build());
        var svc = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        await SeedSolicitantsAsync(db, 20);

        await svc.EvaluateAsync(
            "R",
            db.Solicitants.Where(s => s.IsActive),
            new QueryFilterContext(),
            CancellationToken.None);

        capture.TotalIncrement.Should().Be(1);
    }

    // ───────── Case 9: cancellation passthrough ─────────

    [Fact]
    public async Task EvaluateAsync_CancelledToken_ThrowsOperationCanceled()
    {
        await using var db = CreateContext();
        await SeedSolicitantsAsync(db, 3);
        var svc = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => svc.EvaluateAsync(
            QueryBudgetRegistries.Solicitant,
            db.Solicitants.Where(s => s.IsActive),
            new QueryFilterContext(),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ───────── Helpers ─────────

    /// <summary>
    /// One-off policy resolver returning a single explicit <see cref="QueryBudgetPolicy"/>
    /// for the tests that need to override the default registry budgets. The static
    /// production resolver (<see cref="StaticQueryBudgetPolicy"/>) cannot have its budgets
    /// mutated, so a per-test stub is the cleanest seam.
    /// </summary>
    private sealed class TestPolicyResolver(QueryBudgetPolicy policy) : IQueryBudgetPolicy
    {
        public QueryBudgetPolicy GetForRegistry(string registry) => policy;
    }

    /// <summary>
    /// MeterListener capture for a single instrument name on the CNAS meter. Mirrors the
    /// helper used by the other meter-aware tests in this assembly.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<long> _values = new();
        private readonly object _gate = new();

        public long TotalIncrement
        {
            get { lock (_gate) return _values.Sum(); }
        }

        public MetricCapture(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                lock (_gate)
                {
                    _values.Add(value);
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
