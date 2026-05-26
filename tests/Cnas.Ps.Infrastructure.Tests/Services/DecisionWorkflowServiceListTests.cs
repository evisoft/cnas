using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.QueryBudget;
using Cnas.Ps.Application.UseCases;
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

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0671 continuation — service-level tests for
/// <see cref="DecisionWorkflowService.ListAsync"/>. Mirrors
/// <see cref="DocumentServiceListTests"/>: real budget guard + QBE converter +
/// access-scope filter against an InMemory DB; the access-scope filter narrows the
/// projected Dossier set via the parent ServiceApplication's
/// <see cref="ServiceApplication.SubdivisionCode"/>.
/// </summary>
public sealed class DecisionWorkflowServiceListTests
{
    /// <summary>Subdivision allow-list narrowing the projection to CHISINAU-CENTRU.</summary>
    private static readonly string[] CentralOnly = ["CHISINAU-CENTRU"];

    /// <summary>Empty axes shared between scope-builder calls so CA1825 stays quiet.</summary>
    private static readonly string[] EmptyAxis = Array.Empty<string>();

    /// <summary>Default caller roles wired by <see cref="Build"/>.</summary>
    private static readonly string[] DefaultRoles = ["cnas-decider"];

    private static readonly DateTime BaseUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-decisions-list-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static (DecisionWorkflowService Svc, CnasDbContext Db) Build(
        IAccessScope? scope = null,
        int? budgetOverride = null)
    {
        var db = CreateContext();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

        IQueryBudgetPolicy policy = budgetOverride is { } b
            ? new SingleBudgetPolicy(b)
            : new StaticQueryBudgetPolicy(NullLogger<StaticQueryBudgetPolicy>.Instance);
        var budget = new QueryBudgetService(policy, NullLogger<QueryBudgetService>.Instance);
        var qbeConverter = new QbeToLinqConverter(new QbeRegistrySchemaProvider());
        var accessFilter = new AccessScopeFilter();

        var caller = Substitute.For<ICallerContext>();
        caller.AccessScope.Returns(scope ?? RolesBasedAccessScope.Unscoped);
        caller.UserId.Returns(1L);
        caller.UserSqid.Returns("SQID-1");
        caller.Roles.Returns(DefaultRoles);

        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(BaseUtc);
        var audit = Substitute.For<IAuditService>();
        var mcabinet = Substitute.For<IMCabinetPublisher>();

        var svc = new DecisionWorkflowService(
            db, sqids, clock, caller, audit, mcabinet,
            NullLogger<DecisionWorkflowService>.Instance,
            budget, qbeConverter, accessFilter);
        return (svc, db);
    }

    /// <summary>
    /// Seeds <paramref name="rowCount"/> active application+dossier pairs with a mix of
    /// subdivision codes (half CHISINAU-CENTRU, the rest BALTI) so scope narrowing can
    /// be observed.
    /// </summary>
    private static async Task SeedAsync(CnasDbContext db, int rowCount)
    {
        for (int i = 0; i < rowCount; i++)
        {
            var app = new ServiceApplication
            {
                CreatedAtUtc = BaseUtc.AddDays(i),
                SolicitantId = 0,
                ServicePassportId = 0,
                Status = i % 2 == 0 ? ApplicationStatus.Approved : ApplicationStatus.Rejected,
                FormPayloadJson = "{}",
                IsActive = true,
                SubdivisionCode = i < rowCount / 2 ? "CHISINAU-CENTRU" : "BALTI",
            };
            db.Applications.Add(app);
            await db.SaveChangesAsync();

            db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = BaseUtc.AddDays(i),
                ApplicationId = app.Id,
                DossierNumber = $"D-2026-{i:D5}",
                IsActive = true,
                ClosedAtUtc = i % 2 == 0 ? BaseUtc.AddDays(i).AddHours(1) : null,
                AssignedExaminerId = 100L + i,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ListAsync_ReturnsPagedResultWithSqidIds()
    {
        var (svc, db) = Build();
        await SeedAsync(db, 6);

        var result = await svc.ListAsync(new DecisionsListInput(Take: 50));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(6);
        result.Value.Items.Should().OnlyContain(r => r.Id.StartsWith("SQID-", StringComparison.Ordinal));
        result.Value.Items.Should().OnlyContain(r => r.ServiceApplicationSqid.StartsWith("SQID-", StringComparison.Ordinal));
        result.Value.TotalCount.Should().Be(6);
    }

    [Fact]
    public async Task ListAsync_RespectsQbeFilterOnApplicationId()
    {
        var (svc, db) = Build();
        await SeedAsync(db, 4);
        // Pin first application id so the test can filter against it.
        var firstAppId = await db.Applications.Select(a => a.Id).OrderBy(id => id).FirstAsync();

        var qbe = new QbeFilterDto("AND", new[]
        {
            new QbeConditionDto("ApplicationId", "Equals", firstAppId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        });
        var result = await svc.ListAsync(new DecisionsListInput(Filter: qbe, Take: 50));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListAsync_RespectsAccessScope_ViaParentApplicationSubdivisionCode()
    {
        // Scope = CHISINAU-CENTRU only. Seed 10 rows; 5 CHISINAU-CENTRU + 5 BALTI → 5 visible.
        var scope = new RolesBasedAccessScope(EmptyAxis, CentralOnly, EmptyAxis, EmptyAxis);
        var (svc, db) = Build(scope: scope);
        await SeedAsync(db, 10);

        var result = await svc.ListAsync(new DecisionsListInput(Take: 50));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(5);
    }

    /// <summary>Test stub that returns a single-budget policy regardless of registry.</summary>
    private sealed class SingleBudgetPolicy(int budget) : IQueryBudgetPolicy
    {
        /// <inheritdoc />
        public QueryBudgetPolicy GetForRegistry(string registry) =>
            new(registry, budget, Array.Empty<RefinementHintRule>());
    }
}
