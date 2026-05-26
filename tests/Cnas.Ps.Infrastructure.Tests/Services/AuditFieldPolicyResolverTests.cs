using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0183 / SEC 043 — unit + lightweight integration tests for
/// <see cref="AuditFieldPolicyResolver"/>. Verifies cache hit semantics + the
/// post-CRUD invalidation seam.
/// </summary>
public class AuditFieldPolicyResolverTests
{
    [Fact]
    public async Task Resolve_CacheHit_ReturnsSameInstance()
    {
        using var harness = new Harness();
        await harness.SeedAsync(new AuditFieldPolicy
        {
            EntityType = "Solicitant",
            TrackedFields = new List<string> { "DisplayName" },
            Severity = AuditSeverity.Notice,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        var first = harness.Resolver.Resolve("Solicitant");
        var second = harness.Resolver.Resolve("Solicitant");

        first.Should().NotBeNull();
        // Same record instance is returned because the snapshot stores a single
        // immutable view per EntityType key.
        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_CacheMiss_AfterInvalidate_PicksUpNewRow()
    {
        using var harness = new Harness();
        await harness.Resolver.InvalidateAsync(); // empty initial snapshot

        harness.Resolver.Resolve("Solicitant").Should().BeNull();

        // Insert a row + invalidate.
        await harness.SeedAsync(new AuditFieldPolicy
        {
            EntityType = "Solicitant",
            TrackedFields = new List<string> { "DisplayName" },
            Severity = AuditSeverity.Sensitive,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        var view = harness.Resolver.Resolve("Solicitant");
        view.Should().NotBeNull();
        view!.Severity.Should().Be(AuditSeverity.Sensitive);
        harness.Resolver.SnapshotCount.Should().Be(1);
    }

    [Fact]
    public async Task Resolve_DisabledPolicy_NotReturned()
    {
        using var harness = new Harness();
        await harness.SeedAsync(new AuditFieldPolicy
        {
            EntityType = "Solicitant",
            TrackedFields = new List<string> { "DisplayName" },
            Severity = AuditSeverity.Notice,
            IsEnabled = false,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        harness.Resolver.Resolve("Solicitant").Should().BeNull();
    }

    /// <summary>
    /// Test harness providing an in-memory DB context and a singleton resolver
    /// wired to it via DI. Keeps every test self-contained.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _dbName = $"cnas-fieldpolicy-resolver-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public AuditFieldPolicyResolver Resolver { get; }

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());

            _provider = services.BuildServiceProvider();
            Resolver = new AuditFieldPolicyResolver(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AuditFieldPolicyResolver>.Instance);
        }

        public async Task SeedAsync(AuditFieldPolicy policy)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
            db.AuditFieldPolicies.Add(policy);
            await db.SaveChangesAsync();
        }

        public void Dispose() => _provider.Dispose();
    }
}
