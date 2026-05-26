using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0210 / TOR UI 007 / CF 17.16 — unit tests for <see cref="TranslationResolver"/>.
/// Exercises the (code, language) fallback chain, the RO fallback path, the
/// code-as-fallback final branch, the meter-tag emission on miss, and cache-hit
/// behaviour.
/// </summary>
public class TranslationResolverTests
{
    [Fact]
    public async Task Resolve_ExactHit_ReturnsConfiguredText()
    {
        using var harness = new ResolverHarness();
        await harness.SeedAsync(("pages.applications.list.title", "ro", "Lista cererilor"));
        await harness.Resolver.InvalidateAsync();

        var text = harness.Resolver.Resolve("pages.applications.list.title", "ro");

        text.Should().Be("Lista cererilor");
    }

    [Fact]
    public async Task Resolve_MissingLanguage_RoPresent_FallsBackToRo_AndCounterTicks()
    {
        using var harness = new ResolverHarness();
        await harness.SeedAsync(("pages.applications.list.title", "ro", "Lista cererilor"));
        await harness.Resolver.InvalidateAsync();

        using var meterListener = new MeterValueListener("cnas.translation.miss");

        var text = harness.Resolver.Resolve("pages.applications.list.title", "en");

        text.Should().Be("Lista cererilor");
        meterListener.TotalCount.Should().BeGreaterThan(0,
            "the resolver should emit cnas.translation.miss when the EN lookup falls back to RO");
    }

    [Fact]
    public async Task Resolve_MissingKey_ReturnsFallbackOrCode()
    {
        using var harness = new ResolverHarness();
        await harness.Resolver.InvalidateAsync();

        var withFallback = harness.Resolver.Resolve("pages.nope", "ro", fallback: "FALLBACK!");
        withFallback.Should().Be("FALLBACK!");

        var withoutFallback = harness.Resolver.Resolve("pages.nope", "ro");
        withoutFallback.Should().Be("pages.nope",
            "the resolver returns the code itself when no fallback was supplied — keeps missing strings visible.");
    }

    [Fact]
    public async Task Resolve_CacheHit_DoesNotRequeryDb()
    {
        // RED → GREEN: a second call after seed must NOT trigger another DB load.
        using var harness = new ResolverHarness();
        await harness.SeedAsync(("k1", "ro", "T1"));
        await harness.Resolver.InvalidateAsync();
        var snapshotCountAfterFirstLoad = harness.Resolver.SnapshotCount;

        // Mutate the DB underneath without invalidating — the resolver should not see
        // the change.
        await harness.SeedAsync(("k2", "ro", "T2"));

        var t1 = harness.Resolver.Resolve("k1", "ro");
        var t2 = harness.Resolver.Resolve("k2", "ro");

        t1.Should().Be("T1");
        t2.Should().Be("k2", "cache must be the only source of truth between explicit invalidations");
        harness.Resolver.SnapshotCount.Should().Be(snapshotCountAfterFirstLoad,
            "snapshot must not change without an explicit InvalidateAsync");
    }

    /// <summary>
    /// Counts every observation reported against a named counter on the CNAS meter.
    /// Created per-test so the count is isolated from other tests.
    /// </summary>
    private sealed class MeterValueListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;
        private readonly string _instrument;

        public MeterValueListener(string instrumentName)
        {
            _instrument = instrumentName;
            _listener.InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == CnasMeter.MeterName
                    && string.Equals(inst.Name, _instrument, StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(inst);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (_, measurement, _, _) => Interlocked.Add(ref _total, measurement));
            _listener.Start();
        }

        public long TotalCount => Interlocked.Read(ref _total);
        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// In-memory harness providing a DbContext + resolver wired together.
    /// </summary>
    private sealed class ResolverHarness : IDisposable
    {
        private readonly string _dbName = $"cnas-translation-resolver-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;
        public TranslationResolver Resolver { get; }

        public ResolverHarness()
        {
            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddScoped<IReadOnlyCnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            _provider = services.BuildServiceProvider();
            Resolver = new TranslationResolver(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<TranslationResolver>.Instance);
        }

        public async Task SeedAsync(params (string code, string lang, string text)[] rows)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CnasDbContext>();
            foreach (var (code, lang, text) in rows)
            {
                var existing = await db.TranslationKeys.SingleOrDefaultAsync(k => k.Code == code);
                if (existing is null)
                {
                    existing = new TranslationKey
                    {
                        Code = code,
                        CreatedAtUtc = DateTime.UtcNow,
                        IsActive = true,
                    };
                    db.TranslationKeys.Add(existing);
                    await db.SaveChangesAsync();
                }
                db.TranslationValues.Add(new TranslationValue
                {
                    TranslationKeyId = existing.Id,
                    Language = lang,
                    Text = text,
                    IsApproved = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    IsActive = true,
                });
            }
            await db.SaveChangesAsync();
        }

        public void Dispose() => _provider.Dispose();
    }
}
