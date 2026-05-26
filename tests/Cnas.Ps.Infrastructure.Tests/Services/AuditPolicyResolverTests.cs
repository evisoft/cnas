using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0182 / SEC 042 — unit + lightweight integration tests for
/// <see cref="AuditPolicyResolver"/>. Drives the resolver against an in-memory DB
/// seeded with various policy permutations to exercise the priority ordering,
/// suppression safeguard, regex-DoS guard, and refresh seam.
/// </summary>
/// <remarks>
/// Snapshot atomicity is exercised indirectly — every test calls
/// <see cref="AuditPolicyResolver.InvalidateAsync"/> once at setup so the resolver
/// is hydrated before <see cref="AuditPolicyResolver.Resolve"/> is invoked.
/// </remarks>
public class AuditPolicyResolverTests
{
    [Fact]
    public async Task Resolve_NoPolicies_ReturnsCallerSeverityPassThrough()
    {
        // Arrange — empty DB, no policies at all.
        using var harness = new ResolverHarness();
        await harness.Resolver.InvalidateAsync();

        // Act
        var result = harness.Resolver.Resolve("ANY.EVENT", AuditSeverity.Notice);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.EffectiveSeverity.Should().Be(AuditSeverity.Notice);
        result.Value.Suppress.Should().BeFalse();
        result.Value.ExtraRedactKeys.Should().BeEmpty();
        result.Value.MatchedPolicyCode.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_PicksLowestPriorityMatch_AcrossMultipleCandidates()
    {
        // Arrange — two policies match the same event code, one with Priority 50 and
        // another with Priority 100. The resolver must pick the Priority 50 row.
        using var harness = new ResolverHarness();
        await harness.SeedAsync(new AuditPolicy
        {
            Code = "low-prio",
            Module = "Solicitant",
            Screen = "Search",
            EventCodePattern = "^SOLICITANT\\.VIEW\\.SEARCH$",
            OverrideSeverity = AuditSeverity.Sensitive,
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.SeedAsync(new AuditPolicy
        {
            Code = "high-prio",
            Module = "Solicitant",
            Screen = "Search",
            EventCodePattern = "^SOLICITANT\\.VIEW\\.SEARCH$",
            OverrideSeverity = AuditSeverity.Critical,
            Priority = 50,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        // Act
        var result = harness.Resolver.Resolve(
            "SOLICITANT.VIEW.SEARCH", AuditSeverity.Information, module: "Solicitant", screen: "Search");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value!.MatchedPolicyCode.Should().Be("high-prio");
        result.Value.EffectiveSeverity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public async Task Resolve_PriorityTie_BreaksByIdAscending()
    {
        // Arrange — two policies with identical priority. The lower-Id row was
        // inserted first, so it must win the tie.
        using var harness = new ResolverHarness();
        await harness.SeedAsync(new AuditPolicy
        {
            Code = "first",
            Module = "Solicitant",
            Screen = "Search",
            EventCodePattern = "^SOLICITANT\\.VIEW\\.SEARCH$",
            OverrideSeverity = AuditSeverity.Notice,
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.SeedAsync(new AuditPolicy
        {
            Code = "second",
            Module = "Solicitant",
            Screen = "Search",
            EventCodePattern = "^SOLICITANT\\.VIEW\\.SEARCH$",
            OverrideSeverity = AuditSeverity.Sensitive,
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        // Act
        var result = harness.Resolver.Resolve(
            "SOLICITANT.VIEW.SEARCH", AuditSeverity.Information, "Solicitant", "Search");

        // Assert — first-inserted (lower id) wins.
        result.Value!.MatchedPolicyCode.Should().Be("first");
        result.Value.EffectiveSeverity.Should().Be(AuditSeverity.Notice);
    }

    [Fact]
    public async Task Resolve_OverrideSeverity_LiftsInformationToSensitive()
    {
        // Arrange — caller writes Information; policy says "no actually Sensitive".
        using var harness = new ResolverHarness();
        await harness.SeedAsync(new AuditPolicy
        {
            Code = "pii-read",
            Module = "Solicitant",
            Screen = "Detail",
            DataCategory = "PII",
            EventCodePattern = "^SOLICITANT\\.VIEW\\.DETAIL\\.PII$",
            OverrideSeverity = AuditSeverity.Sensitive,
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        // Act
        var result = harness.Resolver.Resolve(
            "SOLICITANT.VIEW.DETAIL.PII",
            AuditSeverity.Information,
            module: "Solicitant",
            screen: "Detail",
            dataCategory: "PII");

        // Assert
        result.Value!.EffectiveSeverity.Should().Be(AuditSeverity.Sensitive);
    }

    [Fact]
    public async Task Resolve_RegexDos_TimesOut_AndResolverContinues()
    {
        // Arrange — a catastrophically-backtracking pattern (`(a+)+$`) plus a
        // pathological input. The 50 ms timeout must fire and the resolver must
        // return the pass-through projection without crashing.
        using var harness = new ResolverHarness();
        await harness.SeedAsync(new AuditPolicy
        {
            Code = "dos",
            Module = "X",
            Screen = "Y",
            EventCodePattern = "^(a+)+$",
            OverrideSeverity = AuditSeverity.Critical,
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        // Act — long string of 'a' ending in '!' forces backtracking.
        var input = new string('a', 50) + "!";
        var result = harness.Resolver.Resolve(input, AuditSeverity.Information);

        // Assert — resolver did not crash, returned pass-through projection.
        result.IsSuccess.Should().BeTrue();
        result.Value!.MatchedPolicyCode.Should().BeNull();
        result.Value.EffectiveSeverity.Should().Be(AuditSeverity.Information);
    }

    [Fact]
    public async Task Resolve_AfterInvalidate_NewPolicyIsVisible()
    {
        // Arrange — start with no policies.
        using var harness = new ResolverHarness();
        await harness.Resolver.InvalidateAsync();
        var before = harness.Resolver.Resolve("FOO.BAR", AuditSeverity.Information);
        before.Value!.MatchedPolicyCode.Should().BeNull();

        // Add a policy.
        await harness.SeedAsync(new AuditPolicy
        {
            Code = "foo-bar",
            Module = "Foo",
            Screen = "Bar",
            EventCodePattern = "^FOO\\.BAR$",
            OverrideSeverity = AuditSeverity.Notice,
            Priority = 10,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });

        // Act — explicit refresh.
        await harness.Resolver.InvalidateAsync();
        var after = harness.Resolver.Resolve("FOO.BAR", AuditSeverity.Information);

        // Assert
        after.Value!.MatchedPolicyCode.Should().Be("foo-bar");
        after.Value.EffectiveSeverity.Should().Be(AuditSeverity.Notice);
        harness.Resolver.SnapshotCount.Should().Be(1);
    }

    [Fact]
    public async Task Resolve_DisabledPolicy_IsNotConsidered()
    {
        // Arrange — single policy with IsEnabled=false. The resolver must not pick it.
        using var harness = new ResolverHarness();
        await harness.SeedAsync(new AuditPolicy
        {
            Code = "off",
            Module = "Mod",
            Screen = "Scr",
            EventCodePattern = "^ANY$",
            OverrideSeverity = AuditSeverity.Critical,
            Priority = 1,
            IsEnabled = false,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        var result = harness.Resolver.Resolve("ANY", AuditSeverity.Information);

        result.Value!.MatchedPolicyCode.Should().BeNull();
        result.Value.EffectiveSeverity.Should().Be(AuditSeverity.Information);
    }

    [Fact]
    public async Task Resolve_NullCallerFilters_BypassPolicyFilters()
    {
        // Arrange — policy specifies a module+screen but the caller passes null
        // filters. The resolver must still match on event code.
        using var harness = new ResolverHarness();
        await harness.SeedAsync(new AuditPolicy
        {
            Code = "wide",
            Module = "Solicitant",
            Screen = "Search",
            EventCodePattern = "^EVT\\.X$",
            OverrideSeverity = AuditSeverity.Notice,
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        // Caller passes no module/screen — still matches.
        var result = harness.Resolver.Resolve("EVT.X", AuditSeverity.Information);

        result.Value!.MatchedPolicyCode.Should().Be("wide");
        result.Value.EffectiveSeverity.Should().Be(AuditSeverity.Notice);
    }

    /// <summary>
    /// Test harness providing an in-memory DB context, a singleton resolver wired to
    /// it via DI, and a helper for seeding policies. Keeps every test self-contained.
    /// </summary>
    private sealed class ResolverHarness : IDisposable
    {
        private readonly string _dbName = $"cnas-policy-resolver-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public AuditPolicyResolver Resolver { get; }

        public ResolverHarness()
        {
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());

            _provider = services.BuildServiceProvider();
            Resolver = new AuditPolicyResolver(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AuditPolicyResolver>.Instance);
        }

        public async Task SeedAsync(AuditPolicy policy)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
            db.AuditPolicies.Add(policy);
            await db.SaveChangesAsync();
        }

        public void Dispose() => _provider.Dispose();
    }
}
