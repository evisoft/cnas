using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Jobs;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Quartz;

namespace Cnas.Ps.Infrastructure.Tests.Jobs;

/// <summary>
/// Tests for <see cref="SecurityAlertEvaluatorJob"/> — R0189 / SEC 048 Quartz job that
/// scans new <see cref="AuditLog"/> rows past the singleton checkpoint, scores them
/// against the active <see cref="SecurityAlertRule"/> set, and fires alerts when a
/// rule's rolling-window threshold is met and the per-rule cooldown has elapsed.
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> — the job increments the process-static
/// <c>cnas.security_alert.fired</c> counter, so cross-test parallelism must be
/// suppressed to keep increment assertions stable.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class SecurityAlertEvaluatorJobTests
{
    private static readonly DateTime FixedNow = new(2026, 5, 21, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Execute_Disabled_NoOp()
    {
        // Enabled=false → job returns immediately. No DB scan, no rule evaluation,
        // no audit row, no counter increment. We verify by leaving the harness in a
        // state where, if the job DID run, it would otherwise fire — and asserting
        // the audit + notification collaborators were never called.
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = false });
        await harness.SeedRuleAsync("FAILED_LOGIN_BURST", "^USER\\.LOGIN\\.FAIL$", windowSec: 60, threshold: 1);
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
        await harness.Notifications.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default!, default!, default, default);
    }

    [Fact]
    public async Task Execute_StateMissing_LogsAndReturns()
    {
        // The migration seeds the singleton row; if it's missing the job must NOT
        // crash. It logs a warning and returns — no rule evaluation occurs.
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = true });
        var state = await harness.Db.SecurityAlertEvaluatorStates.SingleAsync();
        harness.Db.SecurityAlertEvaluatorStates.Remove(state);
        await harness.Db.SaveChangesAsync();

        await harness.SeedRuleAsync("FAILED_LOGIN_BURST", "^USER\\.LOGIN\\.FAIL$", windowSec: 60, threshold: 1);
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
        harness.Logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Execute_NoRules_NoOp()
    {
        // No rules → no evaluation. The job returns without writing the checkpoint
        // (we don't advance over un-scored rows because a future enable should see
        // them).
        using var capture = new MetricCapture("cnas.security_alert.fired");
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = true });
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
        capture.TotalIncrement.Should().Be(0);

        var state = await harness.Db.SecurityAlertEvaluatorStates.SingleAsync();
        state.LastEvaluatedAuditId.Should().Be(0, "no rules → checkpoint not advanced.");
    }

    [Fact]
    public async Task Execute_BelowThreshold_DoesNotFire()
    {
        // Threshold=10 with 3 matching rows → does NOT fire. The checkpoint still
        // advances past the scanned rows.
        using var capture = new MetricCapture("cnas.security_alert.fired");
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = true });
        await harness.SeedRuleAsync("FAILED_LOGIN_BURST", "^USER\\.LOGIN\\.FAIL$", windowSec: 60, threshold: 10);
        var ids = await harness.SeedAuditManyAsync("USER.LOGIN.FAIL", count: 3);

        await harness.Job.Execute(FakeContext());

        await harness.Audit.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default, default!, default, default, default!, default, default, default);
        capture.TotalIncrement.Should().Be(0);

        var state = await harness.Db.SecurityAlertEvaluatorStates.SingleAsync();
        state.LastEvaluatedAuditId.Should().Be(ids.Max());
    }

    [Fact]
    public async Task Execute_MeetsThreshold_FiresAlert_AndAdvancesCheckpoint()
    {
        // 10 matching rows with threshold=10 → fires. The audit row is written, the
        // counter is bumped, the checkpoint advances, and the rule's LastFiredAtUtc
        // is stamped.
        using var capture = new MetricCapture("cnas.security_alert.fired");
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = true });
        await harness.SeedRuleAsync("FAILED_LOGIN_BURST", "^USER\\.LOGIN\\.FAIL$", windowSec: 60, threshold: 10);
        var ids = await harness.SeedAuditManyAsync("USER.LOGIN.FAIL", count: 10);

        await harness.Job.Execute(FakeContext());

        await harness.Audit.Received(1).RecordAsync(
            eventCode: Arg.Is(SecurityAlertEvaluatorJob.FiredEventCode),
            severity: Arg.Any<AuditSeverity>(),
            actorId: Arg.Is(SecurityAlertEvaluatorJob.SystemActor),
            targetEntity: Arg.Is(nameof(SecurityAlertRule)),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());

        capture.TotalIncrement.Should().Be(1);

        var state = await harness.Db.SecurityAlertEvaluatorStates.SingleAsync();
        state.LastEvaluatedAuditId.Should().Be(ids.Max());

        var rule = await harness.Db.SecurityAlertRules.SingleAsync();
        rule.LastFiredAtUtc.Should().Be(FixedNow);
    }

    [Theory]
    [InlineData("[\\")]
    [InlineData("(unclosed")]
    public async Task Execute_InvalidRulePattern_SkipsRuleAndContinues(string badPattern)
    {
        // A malformed regex must not crash the evaluator. The job logs an error,
        // skips the offending rule, and proceeds. A good neighbouring rule on the
        // same iteration should still fire normally.
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = true });
        await harness.SeedRuleAsync("BAD_PATTERN", badPattern, windowSec: 60, threshold: 1);
        await harness.SeedRuleAsync("GOOD_PATTERN", "^USER\\.LOGIN\\.FAIL$", windowSec: 60, threshold: 1);
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");

        await harness.Job.Execute(FakeContext());

        // The good pattern fired exactly once; the bad pattern was skipped.
        await harness.Audit.Received(1).RecordAsync(
            eventCode: Arg.Is(SecurityAlertEvaluatorJob.FiredEventCode),
            severity: Arg.Any<AuditSeverity>(),
            actorId: Arg.Is(SecurityAlertEvaluatorJob.SystemActor),
            targetEntity: Arg.Any<string?>(),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());

        harness.Logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Execute_Cooldown_PreventsImmediateRefire()
    {
        // Fire once, then immediately re-evaluate with more matching rows → the
        // rule is in cooldown and must NOT re-fire. Counter total stays at 1.
        using var capture = new MetricCapture("cnas.security_alert.fired");
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = true });
        await harness.SeedRuleAsync("FAILED_LOGIN_BURST", "^USER\\.LOGIN\\.FAIL$",
            windowSec: 60, threshold: 1, cooldownSec: 300);
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");

        await harness.Job.Execute(FakeContext());
        capture.TotalIncrement.Should().Be(1);

        // Second fire on the same clock — cooldown still active.
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");
        await harness.Job.Execute(FakeContext());

        capture.TotalIncrement.Should().Be(1, "cooldown blocks the second fire.");
    }

    [Fact]
    public async Task Execute_AfterCooldownElapsed_RefireAllowed()
    {
        // Same setup as the cooldown test, but advance the clock past the cooldown
        // between fires. The rule must re-fire on the second pass.
        using var capture = new MetricCapture("cnas.security_alert.fired");
        var clock = new MutableClock(FixedNow);
        var harness = await Harness.CreateAsync(
            new SecurityAlertOptions { Enabled = true }, clock);
        await harness.SeedRuleAsync("FAILED_LOGIN_BURST", "^USER\\.LOGIN\\.FAIL$",
            windowSec: 60, threshold: 1, cooldownSec: 30);
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");

        await harness.Job.Execute(FakeContext());
        capture.TotalIncrement.Should().Be(1);

        // Advance past cooldown (30 s) + window (60 s) so the second audit row is
        // still inside the new window from the new "now". Seed a fresh row and re-fire.
        clock.UtcNow = FixedNow.AddSeconds(60);
        await harness.SeedAuditAtAsync("USER.LOGIN.FAIL", clock.UtcNow);
        await harness.Job.Execute(FakeContext());

        capture.TotalIncrement.Should().Be(2, "cooldown has elapsed — rule may re-fire.");
    }

    [Fact]
    public async Task Execute_RecipientGroupEmpty_StillFires_LogsWarning()
    {
        // The rule's recipient group resolves to zero users (no UserProfile with
        // the role). The rule still fires (audit + counter + cooldown) but emits a
        // WARN log so operators see the misconfigured role assignment.
        using var capture = new MetricCapture("cnas.security_alert.fired");
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = true });
        await harness.SeedRuleAsync("FAILED_LOGIN_BURST", "^USER\\.LOGIN\\.FAIL$",
            windowSec: 60, threshold: 1, recipientGroup: "nobody-has-this-role");
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");

        await harness.Job.Execute(FakeContext());

        capture.TotalIncrement.Should().Be(1);
        await harness.Audit.Received(1).RecordAsync(
            eventCode: Arg.Is(SecurityAlertEvaluatorJob.FiredEventCode),
            severity: Arg.Any<AuditSeverity>(),
            actorId: Arg.Is(SecurityAlertEvaluatorJob.SystemActor),
            targetEntity: Arg.Any<string?>(),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Any<string>(),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());
        await harness.Notifications.DidNotReceiveWithAnyArgs().EnqueueAsync(
            default, default!, default!, default, default);

        harness.Logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Execute_AlertCounter_TaggedWithRuleCode()
    {
        // The cnas.security_alert.fired counter must carry the rule.code tag so
        // operator dashboards can chart per-rule fire rates.
        using var capture = new TaggedMetricCapture("cnas.security_alert.fired", "rule.code");
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = true });
        await harness.SeedRuleAsync("FAILED_LOGIN_BURST", "^USER\\.LOGIN\\.FAIL$",
            windowSec: 60, threshold: 1);
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");

        await harness.Job.Execute(FakeContext());

        capture.TagValues.Should().Contain("FAILED_LOGIN_BURST");
    }

    [Fact]
    public async Task Execute_Audit_DetailsContainRuleCode_AndMatchCount_AndCountOnly_NoUserIds()
    {
        // The audit payload must carry rule code + counts (no user identifiers).
        // R0185 redaction would scrub PII if present, but the engineered payload
        // here passes redaction untouched.
        var harness = await Harness.CreateAsync(new SecurityAlertOptions { Enabled = true });
        await harness.SeedRuleAsync("FAILED_LOGIN_BURST", "^USER\\.LOGIN\\.FAIL$",
            windowSec: 60, threshold: 1);
        // The harness already auto-seeded a default cnas-admin user inside SeedRuleAsync,
        // so the resolved recipients set carries exactly one row — the SQL has counted
        // it. Adding a SECOND user with the same role would make recipientsCount=2,
        // which is fine but obscures the assertion; we keep the single-user case for
        // clarity.
        await harness.SeedAuditAsync("USER.LOGIN.FAIL");

        string? capturedDetails = null;
        harness.Audit.RecordAsync(
            eventCode: Arg.Any<string>(),
            severity: Arg.Any<AuditSeverity>(),
            actorId: Arg.Any<string>(),
            targetEntity: Arg.Any<string?>(),
            targetEntityId: Arg.Any<long?>(),
            detailsJson: Arg.Do<string>(s => capturedDetails = s),
            sourceIp: Arg.Any<string?>(),
            correlationId: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(Task.FromResult(Result.Success()));

        await harness.Job.Execute(FakeContext());

        capturedDetails.Should().NotBeNull();
        capturedDetails.Should().Contain("\"ruleCode\":\"FAILED_LOGIN_BURST\"");
        capturedDetails.Should().Contain("\"matchCount\":1");
        capturedDetails.Should().Contain("\"recipientsCount\":1");
        // PII discipline — the audit payload carries counts, never individual user
        // identifiers. The keys we DO carry (ruleCode, matchCount, windowSeconds,
        // thresholdCount, recipientsCount) are all bounded vocabulary; no recipient
        // ids, IPs, or emails should slip into the payload.
        capturedDetails.Should().NotContain("recipientId",
            "the audit payload carries counts, not individual user identifiers.");
        capturedDetails.Should().NotContain("recipients\":[",
            "the audit payload must NOT enumerate individual recipient ids.");
    }

    // ─────────────────────── helpers ───────────────────────

    private static IJobExecutionContext FakeContext()
    {
        var ctx = Substitute.For<IJobExecutionContext>();
        ctx.CancellationToken.Returns(CancellationToken.None);
        ctx.FireInstanceId.Returns("fire-test");
        return ctx;
    }

    /// <summary>
    /// MeterListener-based capture for a single counter on the static
    /// <see cref="CnasMeter"/>. Tag-agnostic — sums every increment.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly List<long> _measurements = new();
        private readonly object _gate = new();

        public long TotalIncrement
        {
            get { lock (_gate) return _measurements.Sum(); }
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
                lock (_gate) { _measurements.Add(value); }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// MeterListener-based capture that also records the value of a specific tag on
    /// every measurement. Used to assert the <c>rule.code</c> tag is attached to
    /// the <c>cnas.security_alert.fired</c> counter.
    /// </summary>
    private sealed class TaggedMetricCapture : IDisposable
    {
        private readonly System.Diagnostics.Metrics.MeterListener _listener;
        private readonly string _tagName;
        private readonly List<string> _tagValues = new();
        private readonly object _gate = new();

        public IReadOnlyList<string> TagValues
        {
            get { lock (_gate) return _tagValues.ToList(); }
        }

        public TaggedMetricCapture(string instrumentName, string tagName)
        {
            _tagName = tagName;
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
                    foreach (var t in tags)
                    {
                        if (t.Key == _tagName && t.Value is string s)
                        {
                            _tagValues.Add(s);
                        }
                    }
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Mutable clock — tests advance time between job fires for cooldown coverage.</summary>
    private sealed class MutableClock : ICnasTimeProvider
    {
        public MutableClock(DateTime now) { UtcNow = now; }
        public DateTime UtcNow { get; set; }
    }

    /// <summary>Static fixed clock for the base scenarios.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required SecurityAlertEvaluatorJob Job { get; init; }
        public required IAuditService Audit { get; init; }
        public required INotificationService Notifications { get; init; }
        public required ILogger<SecurityAlertEvaluatorJob> Logger { get; init; }
        public required ICnasTimeProvider Clock { get; init; }

        public static Task<Harness> CreateAsync(SecurityAlertOptions options)
            => CreateAsync(options, new StubClock(FixedNow));

        public static async Task<Harness> CreateAsync(SecurityAlertOptions options, ICnasTimeProvider clock)
        {
            var dbOpts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-secalert-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(dbOpts);

            // Seed the singleton state row (mirroring the migration's seed INSERT).
            db.SecurityAlertEvaluatorStates.Add(new SecurityAlertEvaluatorState
            {
                CreatedAtUtc = FixedNow,
                Key = SecurityAlertEvaluatorJob.SingletonKey,
                LastEvaluatedAuditId = 0,
                IsActive = true,
            });
            await db.SaveChangesAsync();

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                default!, default, default!, default, default, default!, default, default, default)
                .ReturnsForAnyArgs(Task.FromResult(Result.Success()));

            var notifications = Substitute.For<INotificationService>();
            notifications.EnqueueAsync(default, default!, default!, default, default)
                .ReturnsForAnyArgs(Task.FromResult(Result.Success()));

            var logger = Substitute.For<ILogger<SecurityAlertEvaluatorJob>>();
            logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            var scope = Substitute.For<IServiceScope>();
            var sp = Substitute.For<IServiceProvider>();
            sp.GetService(typeof(ICnasDbContext)).Returns(db);
            sp.GetService(typeof(IAuditService)).Returns(audit);
            sp.GetService(typeof(INotificationService)).Returns(notifications);
            sp.GetService(typeof(ICnasTimeProvider)).Returns(clock);
            scope.ServiceProvider.Returns(sp);
            scopeFactory.CreateScope().Returns(scope);

            var job = new SecurityAlertEvaluatorJob(
                scopeFactory,
                new Cnas.Ps.Infrastructure.Tests.Common.AllowAllPeakHourGate(),
                Options.Create(options),
                logger);

            return new Harness
            {
                Db = db,
                Job = job,
                Audit = audit,
                Notifications = notifications,
                Logger = logger,
                Clock = clock,
            };
        }

        /// <summary>
        /// Seeds a single rule + a default <c>cnas-admin</c> user that satisfies the
        /// default recipient group. The user is suppressed for the empty-recipient
        /// scenario by passing a recipient group no user carries.
        /// </summary>
        public async Task SeedRuleAsync(
            string code,
            string pattern,
            int windowSec,
            int threshold,
            int cooldownSec = 0,
            string recipientGroup = "cnas-admin",
            AuditSeverity severity = AuditSeverity.Notice)
        {
            Db.SecurityAlertRules.Add(new SecurityAlertRule
            {
                CreatedAtUtc = FixedNow,
                Code = code,
                EventCodePattern = pattern,
                WindowSeconds = windowSec,
                ThresholdCount = threshold,
                CooldownSeconds = cooldownSec,
                RecipientGroup = recipientGroup,
                AlertSeverity = severity,
                IsActive = true,
            });

            // Auto-seed a default cnas-admin user if none has been seeded yet — keeps
            // the per-test wiring boilerplate-free for the "happy path" cases. Tests
            // that exercise the empty-recipients edge case pass a recipientGroup no
            // user has been seeded for.
            var hasDefaultUser = await Db.UserProfiles.AnyAsync(u => u.Roles.Contains("cnas-admin"));
            if (!hasDefaultUser && recipientGroup == "cnas-admin")
            {
                await SeedUserAsync("cnas-admin");
            }

            await Db.SaveChangesAsync();
        }

        public async Task SeedUserAsync(string role, long? id = null)
        {
            var profile = new UserProfile
            {
                CreatedAtUtc = FixedNow,
                DisplayName = $"User for {role}",
                Roles = new List<string> { role },
                State = UserAccountState.Active,
                IsActive = true,
            };
            Db.UserProfiles.Add(profile);
            await Db.SaveChangesAsync();
        }

        public async Task<long> SeedAuditAsync(string eventCode)
        {
            var row = new AuditLog
            {
                CreatedAtUtc = Clock.UtcNow,
                EventAtUtc = Clock.UtcNow,
                EventCode = eventCode,
                Severity = AuditSeverity.Notice,
                ActorId = "test-actor",
                DetailsJson = "{}",
                PrevHash = "GENESIS",
                RowHash = new string('0', 64),
                IsActive = true,
            };
            Db.AuditLogs.Add(row);
            await Db.SaveChangesAsync();
            return row.Id;
        }

        public async Task<long> SeedAuditAtAsync(string eventCode, DateTime atUtc)
        {
            var row = new AuditLog
            {
                CreatedAtUtc = atUtc,
                EventAtUtc = atUtc,
                EventCode = eventCode,
                Severity = AuditSeverity.Notice,
                ActorId = "test-actor",
                DetailsJson = "{}",
                PrevHash = "GENESIS",
                RowHash = new string('0', 64),
                IsActive = true,
            };
            Db.AuditLogs.Add(row);
            await Db.SaveChangesAsync();
            return row.Id;
        }

        public async Task<List<long>> SeedAuditManyAsync(string eventCode, int count)
        {
            var ids = new List<long>(count);
            for (var i = 0; i < count; i++)
            {
                ids.Add(await SeedAuditAsync(eventCode));
            }
            return ids;
        }
    }
}
