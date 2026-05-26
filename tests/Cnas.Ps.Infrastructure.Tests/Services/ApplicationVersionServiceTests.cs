using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0321 / R0224 / UI 008 — service-level tests for
/// <see cref="ApplicationVersionService"/>. Uses EF Core InMemory + NSubstitute,
/// mirroring the harness shape from <see cref="SavedSearchServiceTests"/>. Each test
/// exercises one branch of the save / revert / list / dedup / cap matrix.
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> because the dedup + autosave-prune
/// branches emit on the process-static <see cref="CnasMeter"/> — cross-class
/// parallelism would inflate the "exactly N increments" assertions.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class ApplicationVersionServiceTests
{
    /// <summary>Deterministic clock anchor used by every test.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Stable application id for tests that share one.</summary>
    private const long DefaultApplicationId = 5001L;

    /// <summary>Caller (citizen) user id.</summary>
    private const long DefaultCallerUserId = 700L;

    /// <summary>Stable Sqid value mapped to <see cref="DefaultApplicationId"/> by the harness stub.</summary>
    private const string DefaultApplicationSqid = "SQID-5001";

    // ─────────────────────── SaveAsync ───────────────────────

    [Fact]
    public async Task SaveAsync_FirstSave_AssignsVersionOneAndIsCurrent_AuditEmitted()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.SaveAsync(
            DefaultApplicationSqid,
            "{\"name\":\"first\"}",
            ApplicationVersionSource.ManualSave,
            note: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.VersionNumber.Should().Be(1);
        result.Value.IsCurrent.Should().BeTrue();

        var row = await harness.Db.ApplicationVersions.SingleAsync();
        row.VersionNumber.Should().Be(1);
        row.IsCurrent.Should().BeTrue();
        row.Source.Should().Be(ApplicationVersionSource.ManualSave);
        row.CreatedByUserId.Should().Be(DefaultCallerUserId);

        // Information-severity audit row emitted (APPLICATION.VERSION.SAVED).
        await harness.Audit.Received().RecordAsync(
            "APPLICATION.VERSION.SAVED",
            AuditSeverity.Information,
            Arg.Any<string>(),
            nameof(ApplicationVersion),
            DefaultApplicationId,
            Arg.Is<string>(d => d.Contains("\"version\":1", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_SecondSave_FlipsPreviousIsCurrent_AndAssignsVersionTwo()
    {
        var harness = await Harness.CreateAsync();
        var first = await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"v\":1}", ApplicationVersionSource.ManualSave, null);
        first.IsSuccess.Should().BeTrue();

        var second = await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"v\":2}", ApplicationVersionSource.ManualSave, null);

        second.IsSuccess.Should().BeTrue();
        second.Value.VersionNumber.Should().Be(2);
        second.Value.IsCurrent.Should().BeTrue();

        var rows = await harness.Db.ApplicationVersions
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].VersionNumber.Should().Be(1);
        rows[0].IsCurrent.Should().BeFalse();
        rows[1].VersionNumber.Should().Be(2);
        rows[1].IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_DedupOnIdenticalPayload_NoNewRow_CounterIncrements()
    {
        using var capture = new MetricCapture("cnas.application_version.dedup");
        var harness = await Harness.CreateAsync();
        var first = await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"hello\":\"world\"}", ApplicationVersionSource.Autosave, null);
        first.IsSuccess.Should().BeTrue();

        // Second save with byte-identical payload → dedup.
        var second = await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"hello\":\"world\"}", ApplicationVersionSource.Autosave, null);

        second.IsSuccess.Should().BeTrue();
        second.Value.VersionNumber.Should().Be(1); // returns the existing current row's version
        (await harness.Db.ApplicationVersions.CountAsync()).Should().Be(1);
        capture.TotalIncrement.Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_AutosaveCapExceeded_PrunesOldestAutosave_KeepsManualAndSubmit()
    {
        using var capture = new MetricCapture("cnas.application_version.autosave_pruned");
        // Tight cap so test stays small/fast.
        var harness = await Harness.CreateAsync(
            opts: new ApplicationAutosaveOptions { MaxAutosavesPerApplication = 2, MaxFormDataKb = 500 });

        // Layout: ManualSave → Autosave(1) → Autosave(2) → Autosave(3 — triggers prune)
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"m\":1}", ApplicationVersionSource.ManualSave, null)).IsSuccess.Should().BeTrue();
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"a\":1}", ApplicationVersionSource.Autosave, null)).IsSuccess.Should().BeTrue();
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"a\":2}", ApplicationVersionSource.Autosave, null)).IsSuccess.Should().BeTrue();
        // This save hits the cap → oldest Autosave (version 2, payload "a":1) is pruned.
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"a\":3}", ApplicationVersionSource.Autosave, null)).IsSuccess.Should().BeTrue();

        var rows = await harness.Db.ApplicationVersions
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();

        // ManualSave (v1) preserved; the oldest Autosave (v2 "a":1) was pruned; v3 + v4 remain.
        rows.Should().HaveCount(3);
        rows.Select(r => r.VersionNumber).Should().Equal(1, 3, 4);
        rows[0].Source.Should().Be(ApplicationVersionSource.ManualSave);
        rows[0].FormDataJson.Should().Be("{\"m\":1}");
        capture.TotalIncrement.Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_OnClosedApplication_ReturnsApplicationNotEditable()
    {
        var harness = await Harness.CreateAsync(applicationStatus: ApplicationStatus.Closed);

        var result = await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"x\":1}", ApplicationVersionSource.Autosave, null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApplicationNotEditable);
        (await harness.Db.ApplicationVersions.CountAsync()).Should().Be(0);
    }

    // ─────────────────────── RevertAsync ───────────────────────

    [Fact]
    public async Task RevertAsync_CreatesNewVersion_WithSourceRevert_AndCopiedPayload()
    {
        var harness = await Harness.CreateAsync();
        var v1 = await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"v\":\"one\"}", ApplicationVersionSource.ManualSave, null);
        var v2 = await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"v\":\"two\"}", ApplicationVersionSource.ManualSave, null);
        v1.IsSuccess.Should().BeTrue();
        v2.IsSuccess.Should().BeTrue();

        var revert = await harness.Service.RevertAsync(DefaultApplicationSqid, targetVersionNumber: 1);

        revert.IsSuccess.Should().BeTrue();
        revert.Value.VersionNumber.Should().Be(3);
        revert.Value.FormDataJson.Should().Be("{\"v\":\"one\"}");
        revert.Value.Source.Should().Be(nameof(ApplicationVersionSource.Revert));
        revert.Value.Note.Should().Contain("Reverted to version 1");
    }

    [Fact]
    public async Task RevertAsync_TargetVersionMissing_ReturnsNotFound()
    {
        var harness = await Harness.CreateAsync();
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{}", ApplicationVersionSource.ManualSave, null)).IsSuccess.Should().BeTrue();

        var revert = await harness.Service.RevertAsync(DefaultApplicationSqid, targetVersionNumber: 99);

        revert.IsFailure.Should().BeTrue();
        revert.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task RevertAsync_EmitsNoticeAudit_WithFromAndTo()
    {
        var harness = await Harness.CreateAsync();
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"a\":1}", ApplicationVersionSource.ManualSave, null)).IsSuccess.Should().BeTrue();
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"a\":2}", ApplicationVersionSource.ManualSave, null)).IsSuccess.Should().BeTrue();

        // Clear prior interactions so we assert just on the revert call.
        harness.Audit.ClearReceivedCalls();

        var revert = await harness.Service.RevertAsync(DefaultApplicationSqid, targetVersionNumber: 1);
        revert.IsSuccess.Should().BeTrue();

        await harness.Audit.Received().RecordAsync(
            "APPLICATION.VERSION.REVERTED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(ApplicationVersion),
            DefaultApplicationId,
            Arg.Is<string>(d => d.Contains("\"from\":1", StringComparison.Ordinal)
                             && d.Contains("\"to\":3", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── ListAsync ───────────────────────

    [Fact]
    public async Task ListAsync_ReturnsDescendingByVersion_AndOmitsFormDataJson()
    {
        var harness = await Harness.CreateAsync();
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"v\":1}", ApplicationVersionSource.ManualSave, null)).IsSuccess.Should().BeTrue();
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"v\":2}", ApplicationVersionSource.Autosave, null)).IsSuccess.Should().BeTrue();
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"v\":3}", ApplicationVersionSource.Autosave, null)).IsSuccess.Should().BeTrue();

        var list = await harness.Service.ListAsync(DefaultApplicationSqid);

        list.IsSuccess.Should().BeTrue();
        list.Value.Select(i => i.VersionNumber).Should().Equal(3, 2, 1);
        // Summary DTO does not even carry a FormDataJson field — assert by checking the
        // type does not declare the property to guard against future widening.
        typeof(ApplicationVersionSummaryDto).GetProperty("FormDataJson").Should().BeNull();
    }

    // ─────────────────────── GetAsync ───────────────────────

    [Fact]
    public async Task GetAsync_ReturnsSingleVersion_WithFormDataJson()
    {
        var harness = await Harness.CreateAsync();
        (await harness.Service.SaveAsync(
            DefaultApplicationSqid, "{\"only\":\"row\"}", ApplicationVersionSource.ManualSave, null)).IsSuccess.Should().BeTrue();

        var result = await harness.Service.GetAsync(DefaultApplicationSqid, versionNumber: 1);

        result.IsSuccess.Should().BeTrue();
        result.Value.FormDataJson.Should().Be("{\"only\":\"row\"}");
        result.Value.VersionNumber.Should().Be(1);
        result.Value.IsCurrent.Should().BeTrue();
    }

    // ─────────────────────── Validator ───────────────────────

    [Fact]
    public void Validator_RejectsEmptyFormDataJson()
    {
        var validator = new ApplicationVersionSaveDtoValidator();
        var dto = new ApplicationVersionSaveDto(
            FormDataJson: string.Empty,
            Source: nameof(ApplicationVersionSource.Autosave),
            Note: null);

        var result = validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ApplicationVersionSaveDto.FormDataJson));
    }

    [Fact]
    public void Validator_RejectsUnparseableJson()
    {
        var validator = new ApplicationVersionSaveDtoValidator();
        var dto = new ApplicationVersionSaveDto(
            FormDataJson: "{not really json",
            Source: nameof(ApplicationVersionSource.Autosave),
            Note: null);

        var result = validator.Validate(dto);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_RejectsOversizedFormDataJson()
    {
        var validator = new ApplicationVersionSaveDtoValidator();
        // Build a JSON string whose UTF-8 byte length exceeds the 500 KB cap.
        // The payload itself is valid JSON.
        var padding = new string('a', (500 * 1024) + 10);
        var dto = new ApplicationVersionSaveDto(
            FormDataJson: $"{{\"k\":\"{padding}\"}}",
            Source: nameof(ApplicationVersionSource.Autosave),
            Note: null);

        var result = validator.Validate(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("KB", StringComparison.Ordinal));
    }

    // ─────────────────────── Harness ───────────────────────

    /// <summary>
    /// Deterministic clock stub honouring <see cref="ICnasTimeProvider"/>. The single
    /// fixed instant means every CreatedAtUtc is identical across a test run, so the
    /// "exactly one current" assertion cannot accidentally pass thanks to a timestamp
    /// race.
    /// </summary>
    /// <param name="now">UTC instant returned for every call.</param>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>MeterListener helper — one instrument name per instance.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<long> _values = new();
        private readonly object _gate = new();

        /// <summary>Sum of every increment seen since construction.</summary>
        public long TotalIncrement
        {
            get { lock (_gate) return _values.Sum(); }
        }

        /// <summary>Subscribes a fresh listener to the named instrument on <see cref="CnasMeter"/>.</summary>
        /// <param name="instrumentName">Fully-qualified instrument name.</param>
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
                lock (_gate) _values.Add(value);
            });
            _listener.Start();
        }

        /// <inheritdoc />
        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// Per-test DI harness. Owns a fresh InMemory store and stubs the surrounding
    /// dependencies. The seeded application defaults to
    /// <see cref="ApplicationStatus.Draft"/> so the editability gate is satisfied.
    /// </summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ApplicationVersionService Service { get; init; }
        public required IAuditService Audit { get; init; }

        /// <summary>
        /// Builds a fully-wired harness with one seeded application in
        /// <paramref name="applicationStatus"/>.
        /// </summary>
        /// <param name="opts">Optional overridden autosave options.</param>
        /// <param name="applicationStatus">Initial status of the seeded application.</param>
        public static async Task<Harness> CreateAsync(
            ApplicationAutosaveOptions? opts = null,
            ApplicationStatus applicationStatus = ApplicationStatus.Draft)
        {
            var db = CreateContext();
            db.Applications.Add(new ServiceApplication
            {
                Id = DefaultApplicationId,
                CreatedAtUtc = ClockNow,
                SolicitantId = DefaultCallerUserId,
                ServicePassportId = 1,
                Status = applicationStatus,
                FormPayloadJson = "{}",
                IsActive = true,
            });
            await db.SaveChangesAsync();

            var clock = new StubClock(ClockNow);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
            {
                var arg = call.Arg<string?>();
                if (!string.IsNullOrEmpty(arg)
                    && arg.StartsWith("SQID-", StringComparison.Ordinal)
                    && long.TryParse(arg.AsSpan(5), out var n))
                {
                    return Result<long>.Success(n);
                }
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
            });

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(DefaultCallerUserId);
            caller.UserSqid.Returns($"SQID-{DefaultCallerUserId}");
            caller.Roles.Returns(["cnas-user"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-test");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var resolvedOpts = Options.Create(opts ?? new ApplicationAutosaveOptions());

            var service = new ApplicationVersionService(db, clock, sqids, audit, caller, resolvedOpts);
            return new Harness { Db = db, Service = service, Audit = audit };
        }
    }

    /// <summary>Creates a fresh EF InMemory context per test.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-appversion-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }
}
