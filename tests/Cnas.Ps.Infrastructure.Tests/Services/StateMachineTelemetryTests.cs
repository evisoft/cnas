using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// End-to-end tests that the pre-declared <see cref="CnasTelemetry"/> counters
/// receive measurements when the dossier state machine performs the matching
/// transition. Each test installs a fresh <see cref="MeterListener"/> subscribed
/// to the <c>cnas.dossiers.*</c> / <c>cnas.documents.*</c> instruments, drives
/// the corresponding service method, and asserts that exactly one measurement
/// with the expected tag value was recorded.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CnasTelemetry"/> exposes process-wide singletons; the same
/// <see cref="Meter"/> instance is reused across every test. Test isolation
/// is preserved by giving every test its own <see cref="MeterListener"/>:
/// listener disposal stops the measurement callbacks for that test.
/// </para>
/// <para>
/// The MeterListener is started inside the test before the service operation
/// runs. Each captured measurement is stored as a
/// <see cref="CapturedMeasurement"/> in a thread-safe list so the assertion
/// phase can inspect both value and tag set.
/// </para>
/// <para>
/// Tests are placed in a non-parallel xUnit collection because the
/// <see cref="Meter"/> is process-wide: a parallel run would cause one
/// test's listener to also observe measurements emitted by other tests
/// targeting the same instrument (e.g. all three rejected-counter tests),
/// breaking the single-measurement assertions.
/// </para>
/// </remarks>
[Collection("CnasTelemetry serial")]
public class StateMachineTelemetryTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);
    private const string PassportCode = "SP-METRIC-TEST";
    private const string DossierSqid = "DOSS-SQID";
    private const string ApplicationSqid = "APP-SQID";
    private const string DocSqid = "DOC-SQID";

    private static readonly string[] DeciderRoles = ["cnas-decider"];
    private static readonly string[] ExaminerRoles = ["cnas-examiner"];
    private static readonly string[] ApplicantRoles = ["cnas-applicant"];
    private static readonly string[] SystemRoles = ["cnas-system"];

    // ─────────────────────── Tests ───────────────────────

    /// <summary>
    /// Successful <see cref="ApplicationProcessingService.AdvanceAsync"/> path —
    /// the engine returns an eligible outcome, the application transitions to
    /// <see cref="ApplicationStatus.UnderExamination"/>, and the
    /// dossiers-accepted-for-examination counter must capture exactly one
    /// measurement tagged with the service passport code.
    /// </summary>
    [Fact]
    public async Task AdvanceAsync_InExaminationTransition_IncrementsDossiersAcceptedForExamination()
    {
        using var capture = new MetricCapture("cnas.dossiers.accepted_for_examination");
        var harness = AdvanceHarness.Create();
        await harness.SeedAsync();
        harness.Engine.Evaluate(Arg.Any<string>(), Arg.Any<DecisionFacts>())
            .Returns(Result<DecisionOutcome>.Success(new DecisionOutcome(
                IsEligible: true,
                Amount: Money.Mdl(1000m),
                ReasonCodes: ["X_ELIGIBLE"],
                ComputedValues: new Dictionary<string, object?>())));

        var result = await harness.Service.AdvanceAsync(ApplicationSqid);

        result.IsSuccess.Should().BeTrue();
        // Filter on the unique service-code tag (PassportCode) so unrelated parallel
        // tests cannot taint the assertion via the shared process-wide Meter singleton.
        capture.Measurements
            .Where(m => m.TagValue("service_code") == PassportCode)
            .Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    /// <summary>
    /// Successful <see cref="DecisionWorkflowService.ApproveAsync"/> path — the
    /// approved counter must capture a single measurement tagged with the
    /// dossier's service code.
    /// </summary>
    [Fact]
    public async Task ApproveAsync_Success_IncrementsDossiersApproved()
    {
        using var capture = new MetricCapture("cnas.dossiers.approved");
        var harness = DecisionHarness.Create(DeciderRoles);
        await harness.SeedAsync();

        var result = await harness.Service.ApproveAsync(DossierSqid, note: null);

        result.IsSuccess.Should().BeTrue();
        // Filter on the unique service-code tag so unrelated parallel tests cannot
        // taint the assertion via the shared process-wide Meter singleton.
        capture.Measurements
            .Where(m => m.TagValue("service_code") == PassportCode)
            .Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    /// <summary>
    /// Successful <see cref="DecisionWorkflowService.RejectAsync"/> path — the
    /// rejected counter must capture a single measurement tagged with
    /// <c>tag=decision</c> so dashboards distinguish decider-driven rejections
    /// from examiner refusals and citizen-initiated withdrawals.
    /// </summary>
    [Fact]
    public async Task RejectAsync_Success_IncrementsDossiersRejectedWithDecisionTag()
    {
        using var capture = new MetricCapture("cnas.dossiers.rejected");
        var harness = DecisionHarness.Create(DeciderRoles);
        await harness.SeedAsync();

        var result = await harness.Service.RejectAsync(DossierSqid, reason: "Missing docs.");

        result.IsSuccess.Should().BeTrue();
        // Filter on BOTH tag and service_code — the rejected counter is shared with
        // parallel WithdrawAsync / RefuseAsync tests, plus pre-existing tests in
        // DecisionWorkflowServiceTests / DecisionWorkflowMCabinetWiringTests that also
        // call RejectAsync (and therefore emit "tag=decision"). The unique
        // PassportCode for this test class isolates the assertion.
        capture.Measurements
            .Where(m => m.TagValue("tag") == "decision"
                        && m.TagValue("service_code") == PassportCode)
            .Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    /// <summary>
    /// Successful <see cref="ApplicationServiceImpl.WithdrawAsync"/> path —
    /// withdrawal is the only solicitant-initiated terminal transition, but
    /// for SLO purposes it still counts toward the rejected counter so the
    /// rolling approval rate stays correct. The <c>tag=withdrawn</c> dimension
    /// lets dashboards split out the citizen-initiated subset.
    /// </summary>
    [Fact]
    public async Task WithdrawAsync_Success_IncrementsDossiersRejectedWithWithdrawnTag()
    {
        using var capture = new MetricCapture("cnas.dossiers.rejected");
        var harness = WithdrawHarness.Create();
        await harness.SeedAsync();

        var result = await harness.Service.WithdrawAsync(ApplicationSqid);

        result.IsSuccess.Should().BeTrue();
        // Filter on BOTH tag and service_code so parallel WithdrawAsync tests in
        // ApplicationServiceWithdrawTests (which would otherwise also emit
        // "tag=withdrawn") cannot pollute this assertion. The unique PassportCode
        // identifies measurements originating from this test class only.
        capture.Measurements
            .Where(m => m.TagValue("tag") == "withdrawn"
                        && m.TagValue("service_code") == PassportCode)
            .Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    /// <summary>
    /// Successful <see cref="DocumentExaminationService.RefuseAsync"/> path —
    /// examiner-driven refusal of a dossier must increment the rejected counter
    /// with <c>tag=examiner-refuse</c> so the operational dashboard can
    /// distinguish the examiner branch from the decider branch.
    /// </summary>
    [Fact]
    public async Task RefuseAsync_Success_IncrementsDossiersRejectedWithExaminerRefuseTag()
    {
        using var capture = new MetricCapture("cnas.dossiers.rejected");
        var harness = ExaminationHarness.Create();
        await harness.SeedAsync(assignedExaminerId: 1L);

        var result = await harness.Service.RefuseAsync(DossierSqid, reason: "Document incomplete.");

        result.IsSuccess.Should().BeTrue();
        // Filter on BOTH tag and service_code so parallel RefuseAsync tests in
        // DocumentExaminationServiceTests / DocumentExaminationMCabinetWiringTests
        // (which would otherwise also emit "tag=examiner-refuse") cannot pollute this
        // assertion. The unique PassportCode identifies measurements originating from
        // this test class only.
        capture.Measurements
            .Where(m => m.TagValue("tag") == "examiner-refuse"
                        && m.TagValue("service_code") == PassportCode)
            .Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    /// <summary>
    /// Sanity check on the <see cref="CnasTelemetry.Meter"/> singleton. Both
    /// the source name and meter name must remain literally <c>"Cnas.Ps.Api"</c>
    /// so the OTel SDK wildcard subscription <c>"Cnas.Ps.*"</c> and all
    /// dashboards pinned to the existing string keep working after the
    /// Api → Infrastructure type relocation.
    /// </summary>
    [Fact]
    public void MeterName_IsCnasPsApi()
    {
        CnasTelemetry.Meter.Name.Should().Be("Cnas.Ps.Api");
        CnasTelemetry.ActivitySource.Name.Should().Be("Cnas.Ps.Api");
    }

    // ─────────────────────── MeterListener capture ───────────────────────

    /// <summary>
    /// One measurement event captured by <see cref="MetricCapture"/>.
    /// </summary>
    /// <param name="Value">The numeric value passed to <c>Add</c> / <c>Record</c>.</param>
    /// <param name="Tags">All key/value tags attached to the measurement.</param>
    private sealed record CapturedMeasurement(long Value, IReadOnlyDictionary<string, object?> Tags)
    {
        /// <summary>Returns the string value of the given tag, or null when absent.</summary>
        public string? TagValue(string key)
            => Tags.TryGetValue(key, out var v) ? v?.ToString() : null;
    }

    /// <summary>
    /// Subscribes a fresh <see cref="MeterListener"/> to the named instrument and
    /// collects every measurement until disposal. The listener is dispose-safe —
    /// disposing it stops the callbacks without throwing.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<CapturedMeasurement> _measurements = [];
        private readonly Lock _gate = new();

        /// <summary>Captured measurements in the order they were emitted.</summary>
        public IReadOnlyList<CapturedMeasurement> Measurements
        {
            get
            {
                lock (_gate)
                {
                    return _measurements.ToList();
                }
            }
        }

        /// <summary>Builds and starts the listener.</summary>
        /// <param name="instrumentName">Exact instrument name to subscribe to (e.g. <c>cnas.dossiers.rejected</c>).</param>
        public MetricCapture(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter == CnasTelemetry.Meter
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
            _listener.Start();
        }

        private void OnLongMeasurement(
            Instrument instrument,
            long measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            // Span cannot be captured into a closure; copy to a dictionary up front.
            var tagSnapshot = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
            foreach (var kv in tags)
            {
                tagSnapshot[kv.Key] = kv.Value;
            }

            lock (_gate)
            {
                _measurements.Add(new CapturedMeasurement(measurement, tagSnapshot));
            }
        }

        /// <inheritdoc />
        public void Dispose() => _listener.Dispose();
    }

    // ─────────────────────── Shared scaffolding ───────────────────────

    /// <summary>Creates a unique EF Core in-memory context per test.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-telemetry-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Deterministic clock returning <see cref="ClockNow"/>.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// Seeds a coherent Solicitant + ServicePassport + ServiceApplication graph
    /// and returns the assigned database keys.
    /// </summary>
    private sealed record SeedResult(long AppId, long SolicitantId, long PassportId, long? DossierId);

    /// <summary>
    /// Common seed helper shared across the per-service harnesses. Creates one
    /// of each entity in the dossier aggregate so every test sees the same
    /// baseline graph.
    /// </summary>
    /// <param name="db">Test DB to seed.</param>
    /// <param name="appStatus">Initial application status.</param>
    /// <param name="includeDossier">Whether to also persist a dossier row (decider/examiner tests need it).</param>
    /// <param name="assignedExaminerId">Optional examiner id for examiner-gated tests.</param>
    private static async Task<SeedResult> SeedCommonAsync(
        CnasDbContext db,
        ApplicationStatus appStatus,
        bool includeDossier,
        long? assignedExaminerId = null)
    {
        var solicitant = new Solicitant
        {
            CreatedAtUtc = ClockNow,
            NationalId = "2000000000007",
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Telemetry Test User",
            PreferredLanguage = "ro",
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);

        var passport = new ServicePassport
        {
            CreatedAtUtc = ClockNow,
            Code = PassportCode,
            NameRo = "Telemetry passport",
            DescriptionRo = "Test",
            FormSchemaJson = "{}",
            WorkflowCode = "WF-TELEMETRY",
            MaxProcessingDays = 30,
            IsEnabled = true,
            DecisionRulesJson = "{\"code\":\"TEST\"}",
            IsActive = true,
        };
        db.ServicePassports.Add(passport);
        await db.SaveChangesAsync();

        var app = new ServiceApplication
        {
            CreatedAtUtc = ClockNow,
            SolicitantId = solicitant.Id,
            ServicePassportId = passport.Id,
            Status = appStatus,
            FormPayloadJson = """{"isInsured":true}""",
            SnapshotJson = "{}",
            SubmittedAtUtc = ClockNow.AddDays(-1),
            ReferenceNumber = "PS-TEL-0001",
            IsActive = true,
        };
        db.Applications.Add(app);
        await db.SaveChangesAsync();

        long? dossierId = null;
        if (includeDossier)
        {
            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = "D-2026-TELEMTRY",
                AssignedExaminerId = assignedExaminerId,
                IsActive = true,
            };
            db.Dossiers.Add(dossier);
            await db.SaveChangesAsync();
            app.DossierId = dossier.Id;
            await db.SaveChangesAsync();
            dossierId = dossier.Id;
        }

        return new SeedResult(app.Id, solicitant.Id, passport.Id, dossierId);
    }

    // ─────────────────────── ApplicationProcessingService harness ───────────────────────

    /// <summary>
    /// Wires <see cref="ApplicationProcessingService"/> with NSubstitute
    /// collaborators so the test can drive
    /// <see cref="ApplicationProcessingService.AdvanceAsync"/> end-to-end.
    /// </summary>
    private sealed class AdvanceHarness
    {
        public required CnasDbContext Db { get; init; }
        public required ApplicationProcessingService Service { get; init; }
        public required IDecisionEngine Engine { get; init; }
        public required ISqidService Sqids { get; init; }

        public static AdvanceHarness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var engine = Substitute.For<IDecisionEngine>();
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var notify = Substitute.For<INotificationService>();
            notify.EnqueueAsync(
                    Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(SystemRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var service = new ApplicationProcessingService(
                db, sqids, clock, engine, audit, notify, caller, mcabinet,
                NullLogger<ApplicationProcessingService>.Instance);

            return new AdvanceHarness
            {
                Db = db,
                Service = service,
                Engine = engine,
                Sqids = sqids,
            };
        }

        public async Task<SeedResult> SeedAsync()
        {
            var seeded = await SeedCommonAsync(Db, ApplicationStatus.Submitted, includeDossier: false);
            Sqids.TryDecode(ApplicationSqid).Returns(Result<long>.Success(seeded.AppId));
            return seeded;
        }
    }

    // ─────────────────────── DecisionWorkflowService harness ───────────────────────

    /// <summary>
    /// Wires <see cref="DecisionWorkflowService"/> for the approve / reject
    /// tests. Caller roles are configurable so the role guard can be
    /// exercised when needed.
    /// </summary>
    private sealed class DecisionHarness
    {
        public required CnasDbContext Db { get; init; }
        public required DecisionWorkflowService Service { get; init; }
        public required ISqidService Sqids { get; init; }

        public static DecisionHarness Create(IReadOnlyCollection<string> roles)
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(roles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var service = new DecisionWorkflowService(
                db, sqids, clock, caller, audit, mcabinet,
                NullLogger<DecisionWorkflowService>.Instance);

            return new DecisionHarness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
            };
        }

        public async Task<SeedResult> SeedAsync()
        {
            var seeded = await SeedCommonAsync(Db, ApplicationStatus.UnderExamination, includeDossier: true);
            Sqids.TryDecode(DossierSqid).Returns(Result<long>.Success(seeded.DossierId!.Value));
            return seeded;
        }
    }

    // ─────────────────────── DocumentExaminationService harness ───────────────────────

    /// <summary>
    /// Wires <see cref="DocumentExaminationService"/> with examiner-role caller
    /// so the refuse / verdict guards pass on the happy path.
    /// </summary>
    private sealed class ExaminationHarness
    {
        public required CnasDbContext Db { get; init; }
        public required DocumentExaminationService Service { get; init; }
        public required ISqidService Sqids { get; init; }

        public static ExaminationHarness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var notify = Substitute.For<INotificationService>();
            notify.EnqueueAsync(
                    Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var docgen = Substitute.For<IDocumentGenerationService>();

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-CALLER");
            caller.UserId.Returns(1L);
            caller.Roles.Returns(ExaminerRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var service = new DocumentExaminationService(
                db, sqids, clock, caller, docgen, audit, notify, mcabinet,
                NullLogger<DocumentExaminationService>.Instance);

            return new ExaminationHarness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
            };
        }

        public async Task<SeedResult> SeedAsync(long assignedExaminerId)
        {
            var seeded = await SeedCommonAsync(
                Db,
                ApplicationStatus.UnderExamination,
                includeDossier: true,
                assignedExaminerId: assignedExaminerId);

            // Seed an attached document so RefuseAsync has something to operate on.
            var doc = new Document
            {
                CreatedAtUtc = ClockNow.AddDays(-2),
                DossierId = seeded.DossierId,
                Kind = DocumentKind.Attachment,
                Title = "id.pdf",
                MimeType = "application/pdf",
                SizeBytes = 100,
                StorageObjectKey = "k",
                StorageBucket = "b",
                ContentSha256Hex = new string('a', 64),
                IsActive = true,
            };
            Db.Documents.Add(doc);
            await Db.SaveChangesAsync();

            Sqids.TryDecode(DossierSqid).Returns(Result<long>.Success(seeded.DossierId!.Value));
            Sqids.TryDecode(DocSqid).Returns(Result<long>.Success(doc.Id));
            return seeded;
        }
    }

    // ─────────────────────── ApplicationServiceImpl harness ───────────────────────

    /// <summary>
    /// Wires <see cref="ApplicationServiceImpl"/> for the withdraw test. The
    /// caller is the owning solicitant so the ownership guard passes.
    /// </summary>
    private sealed class WithdrawHarness
    {
        public required CnasDbContext Db { get; init; }
        public required ApplicationServiceImpl Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required ICallerContext Caller { get; init; }

        public static WithdrawHarness Create()
        {
            var db = CreateContext();
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var notify = Substitute.For<INotificationService>();
            notify.EnqueueAsync(
                    Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var caller = Substitute.For<ICallerContext>();
            caller.UserSqid.Returns("SQID-OWNER");
            caller.Roles.Returns(ApplicantRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var mcabinet = Substitute.For<IMCabinetPublisher>();
            mcabinet.PublishCardAsync(Arg.Any<MCabinetCard>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            // R0570 — always-success examiner-assignment stub so the
            // state-machine telemetry assertions stay focused on the
            // submit/withdraw counters.
            var examinerAssignment = Substitute.For<Cnas.Ps.Application.UseCases.IExaminerAssignmentService>();
            examinerAssignment
                .AssignExaminerAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result<long>.Success(999L)));
            var service = new ApplicationServiceImpl(
                db, sqids, clock, caller, audit, notify, mcabinet,
                NullLogger<ApplicationServiceImpl>.Instance, IdHashHelper.Instance, examinerAssignment);

            return new WithdrawHarness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
                Caller = caller,
            };
        }

        public async Task<SeedResult> SeedAsync()
        {
            var seeded = await SeedCommonAsync(Db, ApplicationStatus.Submitted, includeDossier: false);
            // Caller must own the application for the withdraw guard to pass.
            Caller.UserId.Returns(seeded.SolicitantId);
            Sqids.TryDecode(ApplicationSqid).Returns(Result<long>.Success(seeded.AppId));
            return seeded;
        }
    }
}
