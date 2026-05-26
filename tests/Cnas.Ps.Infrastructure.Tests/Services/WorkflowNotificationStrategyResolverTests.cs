using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.WorkflowNotifications;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0128 / R0173 — unit tests for <see cref="WorkflowNotificationStrategyResolver"/>.
/// Drives the resolver against an in-memory DB seeded with various strategy
/// permutations to exercise the snapshot atomicity, cache invalidation, and no-match
/// contract.
/// </summary>
public class WorkflowNotificationStrategyResolverTests
{
    private static readonly NotificationChannel[] EmailInAppChannels =
        new[] { NotificationChannel.Email, NotificationChannel.InApp };
    private static readonly string[] AssigneeRole = new[] { "Assignee" };
    [Fact]
    public async Task Resolve_NoStrategy_ReturnsNull()
    {
        // Arrange — empty DB.
        using var harness = new ResolverHarness();
        await harness.Resolver.InvalidateAsync();

        // Act
        var view = harness.Resolver.Resolve(workflowDefinitionId: 999, eventCode: WorkflowNotificationEvents.TaskAssigned);

        // Assert
        view.Should().BeNull();
        harness.Resolver.SnapshotCount.Should().Be(0);
    }

    [Fact]
    public async Task Resolve_AfterInvalidate_PicksUpNewlyInsertedStrategy()
    {
        // Arrange — start with empty snapshot.
        using var harness = new ResolverHarness();
        await harness.Resolver.InvalidateAsync();
        var before = harness.Resolver.Resolve(42, WorkflowNotificationEvents.TaskAssigned);
        before.Should().BeNull();

        // Seed a strategy.
        await harness.SeedAsync(new WorkflowNotificationStrategy
        {
            WorkflowDefinitionId = 42,
            EventCode = WorkflowNotificationEvents.TaskAssigned,
            IsEnabled = true,
            Channels = EmailInAppChannels.ToList(),
            RecipientRoles = AssigneeRole.ToList(),
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
        });

        // Act — explicit refresh after CRUD mutation.
        await harness.Resolver.InvalidateAsync();
        var after = harness.Resolver.Resolve(42, WorkflowNotificationEvents.TaskAssigned);

        // Assert — snapshot now contains the row.
        after.Should().NotBeNull();
        after!.IsEnabled.Should().BeTrue();
        after.Channels.Should().BeEquivalentTo(EmailInAppChannels);
        after.RecipientRoles.Should().BeEquivalentTo(AssigneeRole);
        harness.Resolver.SnapshotCount.Should().Be(1);
    }

    /// <summary>
    /// Test harness providing an in-memory DB context + a singleton resolver wired
    /// to it via DI. Keeps every test self-contained.
    /// </summary>
    private sealed class ResolverHarness : IDisposable
    {
        private readonly string _dbName = $"cnas-wf-notify-resolver-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public WorkflowNotificationStrategyResolver Resolver { get; }

        public ResolverHarness()
        {
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());

            _provider = services.BuildServiceProvider();
            Resolver = new WorkflowNotificationStrategyResolver(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkflowNotificationStrategyResolver>.Instance);
        }

        public async Task SeedAsync(WorkflowNotificationStrategy row)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
            db.WorkflowNotificationStrategies.Add(row);
            await db.SaveChangesAsync();
        }

        public void Dispose() => _provider.Dispose();
    }
}
