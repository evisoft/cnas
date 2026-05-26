using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Security;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Observability;

/// <summary>
/// Tests for the <see cref="CnasMeter"/> custom metrics surface (R0040 follow-up). Each
/// test wires a transient <see cref="MeterListener"/> against the well-known meter name
/// <see cref="CnasMeter.MeterName"/> and asserts that the relevant subsystem callsite
/// emits the expected counter increment when its happy path runs. No PII appears in any
/// tag — assertions deliberately read the tags out of the callback so the no-PII
/// invariant is a property of the test, not just of code review.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the production wiring. Because
/// <see cref="Meter"/> instruments are process-static, the listener pattern is the only
/// way to observe them — there is no way to inject a fake counter at a callsite. Each
/// test instantiates its own listener so cross-test isolation is by construction
/// (Dispose unsubscribes from the meter publisher). The
/// <see cref="CnasMeterCollection"/> assignment additionally serialises every other
/// class that emits on the same meter — required to prevent xUnit parallelism from
/// double-counting on shared counters.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public sealed class CnasMeterTests
{
    /// <summary>Deterministic clock anchor for all tests.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    // ─────────────────────── Audit queue / drainer ───────────────────────

    [Fact]
    public async Task AuditService_RecordAsync_OnSuccess_IncrementsAuditEnqueued()
    {
        // RED → GREEN: the AuditService's happy enqueue path must emit exactly one
        // measurement on cnas.audit.enqueued.
        using var capture = new MetricCapture("cnas.audit.enqueued");
        var queue = new AuditWriteQueue();
        var service = new AuditService(queue, new StubClock(ClockNow), NullLogger<AuditService>.Instance);

        var result = await service.RecordAsync(
            eventCode: "TEST.EVT",
            severity: AuditSeverity.Information,
            actorId: "actor",
            targetEntity: "Entity",
            targetEntityId: 1L,
            detailsJson: "{}",
            sourceIp: null,
            correlationId: "corr-1");

        result.IsSuccess.Should().BeTrue();
        capture.TotalIncrement.Should().Be(1, "exactly one record was successfully enqueued.");
        capture.Measurements.Should().AllSatisfy(m =>
            AssertNoPiiTags(m.Tags));
    }

    [Fact]
    public async Task AuditService_RecordAsync_QueueFull_IncrementsAuditDropped_WithReasonQueueFull()
    {
        // Fill the queue to its bound so the next enqueue fails — that's the queue_full
        // drop reason. Tag verifies the reason is the bounded "queue_full" value.
        using var capture = new MetricCapture("cnas.audit.dropped");
        var queue = new AuditWriteQueue();
        var service = new AuditService(queue, new StubClock(ClockNow), NullLogger<AuditService>.Instance);

        for (var i = 0; i < AuditWriteQueue.Capacity; i++)
        {
            queue.TryEnqueue(NewRecord(i.ToString())).Should().BeTrue();
        }

        var overflow = await service.RecordAsync(
            eventCode: "TEST.EVT",
            severity: AuditSeverity.Information,
            actorId: "actor",
            targetEntity: null,
            targetEntityId: null,
            detailsJson: "{}",
            sourceIp: null,
            correlationId: null);

        overflow.IsFailure.Should().BeTrue();
        capture.TotalIncrement.Should().Be(1);
        capture.Measurements.Should().ContainSingle().Which.Tags.Should().Contain(
            new KeyValuePair<string, object?>("reason", "queue_full"));
        capture.Measurements.Should().AllSatisfy(m => AssertNoPiiTags(m.Tags));
    }

    [Fact]
    public async Task AuditDrainer_FlushOnceAsync_OnSuccess_IncrementsAuditFlushed()
    {
        // Drive a single flush of three records — the drainer must emit one
        // cnas.audit.flushed measurement (one per batch) with the batch-size bucket tag.
        using var capture = new MetricCapture("cnas.audit.flushed");
        var queue = new AuditWriteQueue();
        using var harness = new DrainerHarness(queue);

        queue.TryEnqueue(NewRecord("a")).Should().BeTrue();
        queue.TryEnqueue(NewRecord("b")).Should().BeTrue();
        queue.TryEnqueue(NewRecord("c")).Should().BeTrue();

        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        capture.TotalIncrement.Should().Be(1, "one successful flush = one counter increment.");
        // Three records → bucket "5" (1, 5, 10, 50 — pick the next-equal-or-greater).
        capture.Measurements.Should().ContainSingle().Which.Tags
            .Should().Contain(new KeyValuePair<string, object?>("batch.size_bucket", "5"));
        capture.Measurements.Should().AllSatisfy(m => AssertNoPiiTags(m.Tags));
    }

    [Fact]
    public async Task AuditDrainer_FlushOnceAsync_OnFlushFailure_IncrementsAuditArchived()
    {
        // Poison the drainer's SaveChangesAsync so the batch falls into the archive
        // branch. The archive counter must increment exactly once.
        using var capture = new MetricCapture("cnas.audit.archived");
        var queue = new AuditWriteQueue();
        using var harness = new PoisonDrainerHarness(queue);

        queue.TryEnqueue(NewRecord("p1")).Should().BeTrue();
        queue.TryEnqueue(NewRecord("p2")).Should().BeTrue();

        await harness.Drainer.FlushOnceAsync(CancellationToken.None);

        capture.TotalIncrement.Should().Be(1);
        capture.Measurements.Should().AllSatisfy(m => AssertNoPiiTags(m.Tags));
    }

    // ─────────────────────── JWT / refresh tokens ───────────────────────

    [Fact]
    public void JwtTokenIssuer_IssueAccessToken_IncrementsJwtAccessIssued()
    {
        using var capture = new MetricCapture("cnas.jwt.access.issued");
        var options = new JwtOptions
        {
            Issuer = "https://cnas.test",
            Audience = "cnas-api",
            SigningKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            AccessTokenLifetime = TimeSpan.FromMinutes(15),
            RefreshTokenLifetime = TimeSpan.FromDays(30),
        };
        var issuer = new JwtTokenIssuer(Options.Create(options), new StubClock(ClockNow));

        _ = issuer.IssueAccessToken(42L, ["cnas-user"], ["regional-chisinau"]);

        capture.TotalIncrement.Should().Be(1, "issuing one access token = one counter increment.");
        capture.Measurements.Should().AllSatisfy(m => AssertNoPiiTags(m.Tags));
    }

    [Fact]
    public async Task RefreshTokenService_RotateAsync_OnReuse_IncrementsRefreshReuseDetected()
    {
        // The headline security counter. Reuse-detection requires: issue → rotate → rotate
        // again with the SAME original plaintext (now ConsumedAtUtc != null).
        using var capture = new MetricCapture("cnas.refresh.reuse_detected");
        var harness = await RefreshHarness.CreateAsync();

        var first = (await harness.Service.IssueAsync(RefreshHarness.UserId)).Value;
        var rotated = await harness.Service.RotateAsync(first.OpaqueToken);
        rotated.IsSuccess.Should().BeTrue("first rotation works.");

        // Re-present the original (now-consumed) token — this is the reuse path.
        var reuse = await harness.Service.RotateAsync(first.OpaqueToken);

        reuse.IsFailure.Should().BeTrue();
        reuse.ErrorCode.Should().Be(ErrorCodes.RefreshTokenReused);
        capture.TotalIncrement.Should().Be(1, "reuse detection emits exactly one counter increment.");
        capture.Measurements.Should().ContainSingle().Which.Tags.Should().Contain(
            new KeyValuePair<string, object?>("family.revoked", true));
        capture.Measurements.Should().AllSatisfy(m => AssertNoPiiTags(m.Tags));
    }

    // ─────────────────────── Admin actions ───────────────────────

    [Fact]
    public async Task PendingAdminActionService_SubmitAsync_OnSuccess_IncrementsAdminActionSubmitted()
    {
        using var capture = new MetricCapture("cnas.admin.action.submitted");
        var harness = AdminActionHarness.Create();

        var result = await harness.Service.SubmitAsync("DEMO.NOOP", "{}");

        result.IsSuccess.Should().BeTrue();
        capture.TotalIncrement.Should().Be(1);
        capture.Measurements.Should().AllSatisfy(m => AssertNoPiiTags(m.Tags));
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>
    /// Asserts that the supplied tag list contains none of the well-known PII keys we
    /// banned (CLAUDE.md §5.6). Tag keys carrying user identifiers, IDNPs, IP addresses,
    /// or token hashes would defeat the cardinality bound and leak production data into
    /// the metrics pipeline — both are unacceptable.
    /// </summary>
    private static void AssertNoPiiTags(IReadOnlyList<KeyValuePair<string, object?>> tags)
    {
        foreach (var kv in tags)
        {
            kv.Key.Should().NotContain("user", "tags must not carry user identifiers.");
            kv.Key.Should().NotContain("idnp", "tags must not carry IDNPs.");
            kv.Key.Should().NotContain("email", "tags must not carry email addresses.");
            kv.Key.Should().NotContain("hash", "tags must not carry hash material.");
        }
    }

    /// <summary>Builds a stock <see cref="AuditEventRecord"/> with no PII.</summary>
    private static AuditEventRecord NewRecord(string correlationId, string eventCode = "TEST.EVT")
        => new(
            EventCode: eventCode,
            Severity: AuditSeverity.Information,
            ActorId: "actor",
            TargetEntity: "Entity",
            TargetEntityId: 1L,
            DetailsJson: "{}",
            SourceIp: "127.0.0.1",
            CorrelationId: correlationId,
            EventAtUtc: ClockNow);

    /// <summary>Deterministic clock for tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// MeterListener-based capture for a single instrument name on
    /// <see cref="CnasMeter.MeterName"/>. Disposes the listener at the end of the test
    /// so the next test starts from a clean slate.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Measurement> _measurements = new();
        private readonly object _gate = new();

        public IReadOnlyList<Measurement> Measurements
        {
            get { lock (_gate) return _measurements.ToList(); }
        }

        public long TotalIncrement
        {
            get { lock (_gate) return _measurements.Sum(m => m.Value); }
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
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (_gate)
                {
                    _measurements.Add(new Measurement(value, tags.ToArray()));
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        public sealed record Measurement(long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);
    }

    /// <summary>
    /// Drainer harness mirroring <see cref="Services.AuditDrainerTests"/> — wires an
    /// in-memory DbContext, a substituted MLog, and a substituted archive.
    /// </summary>
    private sealed class DrainerHarness : IDisposable
    {
        private readonly string _dbName = $"cnas-meter-drainer-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public AuditDrainer Drainer { get; }
        public IMLogClient Mlog { get; }
        public IAuditArchive Archive { get; }

        public DrainerHarness(AuditWriteQueue queue)
        {
            Mlog = Substitute.For<IMLogClient>();
            Mlog.AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            Archive = Substitute.For<IAuditArchive>();
            Archive.ArchiveAsync(Arg.Any<IReadOnlyList<AuditEventRecord>>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddDbContext<CnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<CnasDbContext>());
            services.AddSingleton(Mlog);

            _provider = services.BuildServiceProvider();
            Drainer = new AuditDrainer(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Archive,
                NullLogger<AuditDrainer>.Instance);
        }

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// Drainer harness whose <see cref="CnasDbContext.SaveChangesAsync"/> always throws.
    /// Used to exercise the archive branch of <see cref="AuditDrainer.FlushOnceAsync"/>.
    /// </summary>
    private sealed class PoisonDrainerHarness : IDisposable
    {
        private readonly string _dbName = $"cnas-meter-poison-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public AuditDrainer Drainer { get; }
        public IAuditArchive Archive { get; }

        public PoisonDrainerHarness(AuditWriteQueue queue)
        {
            var mlog = Substitute.For<IMLogClient>();
            mlog.AppendAsync(Arg.Any<MLogEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            Archive = Substitute.For<IAuditArchive>();
            Archive.ArchiveAsync(Arg.Any<IReadOnlyList<AuditEventRecord>>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddDbContext<PoisonCnasDbContext>(opts => opts
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddScoped<ICnasDbContext>(sp => sp.GetRequiredService<PoisonCnasDbContext>());
            services.AddSingleton(mlog);

            _provider = services.BuildServiceProvider();
            Drainer = new AuditDrainer(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Archive,
                NullLogger<AuditDrainer>.Instance);
        }

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// CnasDbContext subclass that throws on <see cref="SaveChangesAsync"/>. Mirrors the
    /// pattern used by <see cref="Services.AuditDrainerTests"/> but always-on.
    /// </summary>
    private sealed class PoisonCnasDbContext : CnasDbContext
    {
        public PoisonCnasDbContext(DbContextOptions<PoisonCnasDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated DB failure");
    }

    /// <summary>Refresh-token harness mirroring <see cref="Security.RefreshTokenServiceTests"/>.</summary>
    private sealed class RefreshHarness
    {
        public const long UserId = 7L;

        public required CnasDbContext Db { get; init; }
        public required RefreshTokenService Service { get; init; }

        public static async Task<RefreshHarness> CreateAsync()
        {
            var db = CreateContext();
            db.UserProfiles.Add(new UserProfile
            {
                Id = UserId,
                DisplayName = "Test User",
                State = UserAccountState.Active,
                CreatedAtUtc = ClockNow.AddDays(-100),
                IsActive = true,
            });
            await db.SaveChangesAsync();

            var options = new JwtOptions
            {
                Issuer = "https://cnas.test",
                Audience = "cnas-api",
                SigningKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                AccessTokenLifetime = TimeSpan.FromMinutes(15),
                RefreshTokenLifetime = TimeSpan.FromDays(30),
            };
            var service = new RefreshTokenService(
                db,
                new StubClock(ClockNow),
                Options.Create(options),
                NullLogger<RefreshTokenService>.Instance);
            return new RefreshHarness { Db = db, Service = service };
        }

        private static CnasDbContext CreateContext()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-meter-refresh-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new CnasDbContext(opts);
        }
    }

    /// <summary>Admin-action harness mirroring <see cref="Services.PendingAdminActionServiceTests"/>.</summary>
    private sealed class AdminActionHarness
    {
        public const long MakerUserId = 1001L;

        public required CnasDbContext Db { get; init; }
        public required PendingAdminActionService Service { get; init; }

        public static AdminActionHarness Create()
        {
            var db = new CnasDbContext(new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-meter-admin-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(MakerUserId);
            caller.UserSqid.Returns("SQID-MAKER");
            caller.Roles.Returns(["cnas-admin"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-test");

            IEnumerable<IPendingAdminActionExecutor> executors = [new NoOpDemoExecutor()];

            var service = new PendingAdminActionService(db, sqids, new StubClock(ClockNow), caller, executors);
            return new AdminActionHarness { Db = db, Service = service };
        }
    }
}
