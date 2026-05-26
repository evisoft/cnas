using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0182 / SEC 042 — end-to-end tests that drive <see cref="AuditDrainer"/> through
/// a single flush cycle with a real <see cref="AuditPolicyResolver"/> wired into
/// the scope. Exercises the three policy levers (override severity, suppression,
/// extra redact keys) at the actual persistence boundary so the chain hash also
/// reflects the resolved shape.
/// </summary>
[Collection(CnasMeterCollection.Name)]
public class AuditDrainerPolicyIntegrationTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Drainer_PolicySuppressesInformationRow_AndIncrementsCounter()
    {
        // Arrange
        using var capture = new MetricCapture("cnas.audit.policy_suppressed");
        var queue = new AuditWriteQueue();
        using var harness = new PolicyDrainerHarness(queue);
        await harness.SeedPolicyAsync(new AuditPolicy
        {
            Code = "noisy-info",
            Module = "Mod",
            Screen = "Scr",
            EventCodePattern = "^NOISY\\.INFO$",
            OverrideSeverity = null,
            SuppressAudit = true,
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        queue.TryEnqueue(NewRecord("EVT.A", "NOISY.INFO", AuditSeverity.Information)).Should().BeTrue();

        // Act
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Assert — no AuditLog row written; counter incremented exactly once.
        var db = harness.NewDb();
        (await db.AuditLogs.CountAsync()).Should().Be(0);
        capture.TotalIncrement.Should().Be(1);
    }

    [Fact]
    public async Task Drainer_PolicyAttemptsToSuppressCritical_Refused_RowWritten_MisconfigCounterFires()
    {
        // Arrange — policy says SuppressAudit=true but the caller wrote Critical.
        // Defense in depth must REFUSE the suppression and still persist the row.
        using var capture = new MetricCapture("cnas.audit.policy_misconfig");
        var queue = new AuditWriteQueue();
        using var harness = new PolicyDrainerHarness(queue);
        await harness.SeedPolicyAsync(new AuditPolicy
        {
            Code = "bad-suppress",
            Module = "Mod",
            Screen = "Scr",
            EventCodePattern = "^IMPORTANT$",
            OverrideSeverity = null,
            SuppressAudit = true,
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        queue.TryEnqueue(NewRecord("EVT.B", "IMPORTANT", AuditSeverity.Critical)).Should().BeTrue();

        // Act
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Assert — row persisted; misconfig counter fired.
        var db = harness.NewDb();
        (await db.AuditLogs.CountAsync()).Should().Be(1);
        capture.TotalIncrement.Should().Be(1);
    }

    [Fact]
    public async Task Drainer_PolicyOverrideSeverity_AppliesBeforePersist()
    {
        // Arrange — caller wrote Information, policy lifts to Sensitive.
        var queue = new AuditWriteQueue();
        using var harness = new PolicyDrainerHarness(queue);
        await harness.SeedPolicyAsync(new AuditPolicy
        {
            Code = "pii-lift",
            Module = "Mod",
            Screen = "Scr",
            EventCodePattern = "^PII\\.READ$",
            OverrideSeverity = AuditSeverity.Sensitive,
            SuppressAudit = false,
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        queue.TryEnqueue(NewRecord("EVT.C", "PII.READ", AuditSeverity.Information)).Should().BeTrue();

        // Act
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Assert — persisted row carries the lifted severity.
        var db = harness.NewDb();
        var row = await db.AuditLogs.SingleAsync();
        row.Severity.Should().Be(AuditSeverity.Sensitive);
    }

    [Fact]
    public async Task Drainer_PolicyExtraRedactKeys_AreMergedBeforeHash()
    {
        // Arrange — policy adds 'customField' to the redact set. The persisted
        // DetailsJson must show '[redacted]' for that key (and the row's RowHash
        // chains on the redacted form, not the raw payload).
        var queue = new AuditWriteQueue();
        using var harness = new PolicyDrainerHarness(queue);
        await harness.SeedPolicyAsync(new AuditPolicy
        {
            Code = "redact-extra",
            Module = "Mod",
            Screen = "Scr",
            EventCodePattern = "^REDACT\\.NOW$",
            OverrideSeverity = null,
            SuppressAudit = false,
            ExtraRedactKeys = new List<string> { "customField" },
            Priority = 100,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await harness.Resolver.InvalidateAsync();

        var json = "{\"customField\":\"sensitive-value\",\"other\":\"ok\"}";
        queue.TryEnqueue(NewRecord("EVT.D", "REDACT.NOW", AuditSeverity.Notice, json)).Should().BeTrue();

        // Act
        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        // Assert — DetailsJson reflects the extra-redact substitution.
        var db = harness.NewDb();
        var row = await db.AuditLogs.SingleAsync();
        row.DetailsJson.Should().Contain("[redacted]");
        row.DetailsJson.Should().NotContain("sensitive-value");
        row.DetailsJson.Should().Contain("\"other\":\"ok\"");
    }

    private static AuditEventRecord NewRecord(
        string correlationId,
        string eventCode,
        AuditSeverity severity,
        string detailsJson = "{}") =>
        new(
            EventCode: eventCode,
            Severity: severity,
            ActorId: "actor:test",
            TargetEntity: "Test",
            TargetEntityId: 1L,
            DetailsJson: detailsJson,
            SourceIp: "127.0.0.1",
            CorrelationId: correlationId,
            EventAtUtc: ClockNow);

    /// <summary>
    /// MeterListener helper capturing increments on a single instrument name.
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

    /// <summary>
    /// Harness wiring the drainer with a real <see cref="AuditPolicyResolver"/>
    /// registered in the scope so the integration path is exercised end-to-end.
    /// </summary>
    private sealed class PolicyDrainerHarness : IDisposable
    {
        private readonly string _dbName = $"cnas-drainer-policy-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;
        public AuditDrainer Drainer { get; }
        public IMLogClient Mlog { get; }
        public AuditPolicyResolver Resolver { get; }

        public PolicyDrainerHarness(AuditWriteQueue queue)
        {
            Mlog = Substitute.For<IMLogClient>();
            Mlog.AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var archive = Substitute.For<IAuditArchive>();

            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddSingleton(Mlog);

            _provider = services.BuildServiceProvider();

            Resolver = new AuditPolicyResolver(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AuditPolicyResolver>.Instance);

            // Register the resolver under both the singleton type AND the interface
            // so the drainer can fetch it from the scope.
            var enriched = new ServiceCollection();
            enriched.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            enriched.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            enriched.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            enriched.AddSingleton(Mlog);
            enriched.AddSingleton(Resolver);
            enriched.AddSingleton<IAuditPolicyResolver>(Resolver);
            _provider = enriched.BuildServiceProvider();

            Drainer = new AuditDrainer(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                archive,
                NullLogger<AuditDrainer>.Instance);
        }

        public async Task SeedPolicyAsync(AuditPolicy policy)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
            db.AuditPolicies.Add(policy);
            await db.SaveChangesAsync();
        }

        public CnasDbContext NewDb() => _provider.CreateScope()
            .ServiceProvider.GetRequiredService<CnasDbContext>();

        public void Dispose() => _provider.Dispose();
    }
}
