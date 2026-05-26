using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Etl;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Etl;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Etl;

/// <summary>
/// R0153 / TOR CF 19.05 — integration-style tests for
/// <see cref="ContributorPeriodProjectionService"/>. Each test seeds the
/// supersession child tables, runs the rebuild, and asserts the slice
/// inventory persisted into <c>ContributorPeriodProjections</c>.
/// </summary>
[Collection(CnasMeterCollection.Name)]
public sealed class ContributorPeriodProjectionServiceTests
{
    private static readonly DateTime Anchor = new(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RebuildForContributor_WritesExpectedSlices_ForTwoAddressesAndOneContact()
    {
        var harness = Harness.Create();
        var contributorId = await SeedContributorAsync(harness.Db);
        await SeedAddressAsync(harness.Db, contributorId,
            city: "Chișinău", region: "Chișinău", country: "MD",
            from: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAddressAsync(harness.Db, contributorId,
            city: "Bălți", region: "Bălți", country: "MD",
            from: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: null);
        await SeedContactAsync(harness.Db, contributorId,
            phone: "+37360123456", email: "user@example.md",
            from: new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), to: null);

        var result = await harness.Service.RebuildForContributorAsync(contributorId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SlicesCreated.Should().Be(3);
        result.Value.ContributorsProcessed.Should().Be(1);

        var rows = await harness.Db.ContributorPeriodProjections
            .AsNoTracking()
            .OrderBy(r => r.PeriodStartUtc)
            .ToListAsync();
        rows.Should().HaveCount(3);
        rows[0].AddressCity.Should().Be("Chișinău");
        rows[0].PhoneE164.Should().BeNull();
        rows[1].AddressCity.Should().Be("Chișinău");
        rows[1].PhoneE164.Should().Be("+37360123456");
        rows[2].AddressCity.Should().Be("Bălți");
        rows[2].PeriodEndUtc.Should().Be(DateTime.MaxValue);
    }

    [Fact]
    public async Task RebuildForContributor_IsIdempotent_ReRunProducesSameSet()
    {
        var harness = Harness.Create();
        var contributorId = await SeedContributorAsync(harness.Db);
        await SeedAddressAsync(harness.Db, contributorId,
            city: "Chișinău", region: "Chișinău", country: "MD",
            from: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), to: null);

        var first = await harness.Service.RebuildForContributorAsync(contributorId, CancellationToken.None);
        var second = await harness.Service.RebuildForContributorAsync(contributorId, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        first.Value.SlicesCreated.Should().Be(second.Value.SlicesCreated);

        var rows = await harness.Db.ContributorPeriodProjections.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(first.Value.SlicesCreated);
    }

    [Fact]
    public async Task RebuildForContributor_WipesPriorProjectionRows_BeforeReInsert()
    {
        var harness = Harness.Create();
        var contributorId = await SeedContributorAsync(harness.Db);

        // Pre-seed a stale projection row that should be wiped.
        harness.Db.ContributorPeriodProjections.Add(new ContributorPeriodProjection
        {
            ContributorId = contributorId,
            PeriodStartUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodEndUtc = new(2000, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            AddressCity = "STALE",
            ProjectedAtUtc = Anchor,
            CreatedAtUtc = Anchor,
            IsActive = true,
        });
        await harness.Db.SaveChangesAsync();

        await SeedAddressAsync(harness.Db, contributorId,
            city: "Fresh", region: "MD", country: "MD",
            from: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), to: null);

        await harness.Service.RebuildForContributorAsync(contributorId, CancellationToken.None);

        var rows = await harness.Db.ContributorPeriodProjections.AsNoTracking().ToListAsync();
        rows.Should().NotContain(r => r.AddressCity == "STALE");
        rows.Should().ContainSingle().Which.AddressCity.Should().Be("Fresh");
    }

    [Fact]
    public async Task RebuildAll_IteratesEveryContributor_AndAuditsTheRun()
    {
        var harness = Harness.Create();
        var c1 = await SeedContributorAsync(harness.Db, idnp: "1111111111111");
        var c2 = await SeedContributorAsync(harness.Db, idnp: "2222222222222");
        await SeedAddressAsync(harness.Db, c1, city: "Chișinău", region: "MD", country: "MD",
            from: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), to: null);
        await SeedAddressAsync(harness.Db, c2, city: "Bălți", region: "MD", country: "MD",
            from: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), to: null);

        var result = await harness.Service.RebuildAllAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ContributorsProcessed.Should().Be(2);
        result.Value.SlicesCreated.Should().Be(2);
        result.Value.ContributorSqid.Should().BeNull();

        await harness.Audit.Received(1).RecordAsync(
            "ETL.PERIOD_PROJECTION.COMPLETED",
            AuditSeverity.Information,
            Arg.Any<string>(),
            nameof(ContributorPeriodProjection),
            Arg.Any<long?>(),
            Arg.Is<string>(d => d.Contains("\"contributorsProcessed\":2", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Query_ReturnsSliceContainingAsOfUtc()
    {
        var harness = Harness.Create();
        var contributorId = await SeedContributorAsync(harness.Db);
        await SeedAddressAsync(harness.Db, contributorId,
            city: "Chișinău", region: "MD", country: "MD",
            from: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAddressAsync(harness.Db, contributorId,
            city: "Bălți", region: "MD", country: "MD",
            from: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: null);
        await harness.Service.RebuildForContributorAsync(contributorId, CancellationToken.None);

        var midPeriod = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var dtos = await harness.Service.QueryAsync(contributorId, midPeriod, CancellationToken.None);

        dtos.Should().ContainSingle()
            .Which.AddressCity.Should().Be("Chișinău");
    }

    [Fact]
    public async Task Query_ReturnsEmpty_WhenNoProjectionCoversAsOfUtc()
    {
        var harness = Harness.Create();
        var contributorId = await SeedContributorAsync(harness.Db);
        // Single closed slice — query a date outside it.
        await SeedAddressAsync(harness.Db, contributorId,
            city: "Chișinău", region: "MD", country: "MD",
            from: new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await harness.Service.RebuildForContributorAsync(contributorId, CancellationToken.None);

        var farFuture = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dtos = await harness.Service.QueryAsync(contributorId, farFuture, CancellationToken.None);

        dtos.Should().BeEmpty();
    }

    [Fact]
    public async Task RebuildAll_OnSuccess_IncrementsRunCounterWithSuccessOutcome()
    {
        using var capture = new MetricCapture("cnas.etl.contributor_projection_run");
        var harness = Harness.Create();

        await harness.Service.RebuildAllAsync(CancellationToken.None);

        capture.TotalIncrement.Should().Be(1);
        capture.Tags.Should().Contain(t =>
            t.Any(kv => kv.Key == "outcome" && kv.Value as string == "success"));
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>Seeds an InsuredPerson and returns its raw id.</summary>
    private static async Task<long> SeedContributorAsync(CnasDbContext db, string idnp = "1234567890123")
    {
        var p = new InsuredPerson
        {
            Idnp = idnp,
            IdnpHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(idnp))),
            LastName = "Test",
            FirstName = "User",
            BirthDate = new(1990, 1, 1),
            RegisteredAtUtc = Anchor,
            CreatedAtUtc = Anchor,
            IsActive = true,
        };
        db.InsuredPersons.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>Seeds a ContributorAddress row.</summary>
    private static async Task SeedAddressAsync(
        CnasDbContext db, long contributorId,
        string city, string region, string country,
        DateTime from, DateTime? to)
    {
        db.ContributorAddresses.Add(new ContributorAddress
        {
            ContributorId = contributorId,
            Street = "Test 1",
            City = city,
            Region = region,
            PostalCode = "MD-2000",
            Country = country,
            ValidFromUtc = from,
            ValidToUtc = to,
            CreatedAtUtc = from,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds a ContributorContact row.</summary>
    private static async Task SeedContactAsync(
        CnasDbContext db, long contributorId,
        string? phone, string? email,
        DateTime from, DateTime? to)
    {
        db.ContributorContacts.Add(new ContributorContact
        {
            ContributorId = contributorId,
            PhoneE164 = phone,
            Email = email,
            ValidFromUtc = from,
            ValidToUtc = to,
            CreatedAtUtc = from,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>MeterListener-based capture for a single instrument name.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly List<long> _measurements = new();
        private readonly List<IReadOnlyList<KeyValuePair<string, object?>>> _tags = new();
        private readonly object _gate = new();

        public long TotalIncrement
        {
            get { lock (_gate) return _measurements.Sum(); }
        }

        public IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Tags
        {
            get { lock (_gate) return _tags.ToList(); }
        }

        public MetricCapture(string instrumentName)
        {
            _listener = new System.Diagnostics.Metrics.MeterListener
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
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_gate)
                {
                    _measurements.Add(value);
                    _tags.Add(tags.ToArray());
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Stub clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Test fixture wiring the projection service against an InMemory DbContext.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required IContributorPeriodProjectionService Service { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-etl-projection-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var clock = new StubClock(Anchor);

            var service = new ContributorPeriodProjectionService(
                db, clock, sqids, audit,
                NullLogger<ContributorPeriodProjectionService>.Instance);

            return new Harness { Db = db, Service = service, Audit = audit };
        }
    }
}
