using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0535 / CF 04.07-08 — unit tests for <see cref="UserLayoutPreferencesService"/>.
/// Pins six invariants required by CLAUDE.md RULE 1 (tests first):
/// <list type="bullet">
///   <item>GetForCurrentUser returns the system defaults when the column is NULL.</item>
///   <item>Save → Get round-trips the persisted JSON.</item>
///   <item>Save emits an Information audit row with event code <c>USER.LAYOUT.UPDATED</c>.</item>
///   <item>Malformed JSON in the column falls back to defaults AND increments
///         <c>cnas.user_layout.parse_failure</c>.</item>
///   <item>An unauthenticated caller cannot Save (returns Unauthorized).</item>
///   <item>A deactivated user row produces NotFound on Save.</item>
/// </list>
/// </summary>
public sealed class UserLayoutPreferencesServiceTests
{
    /// <summary>Deterministic UTC clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetForCurrentUser_NullColumn_ReturnsDefaults()
    {
        // Arrange — a user row with LayoutPreferences = null (the post-migration state).
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "layout-null",
            DisplayName = "Default Layout",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        // Act
        var dto = await harness.Service.GetForCurrentUserAsync();

        // Assert — defaults: empty grids, system page size, empty widget order.
        dto.Grids.Should().BeEmpty();
        dto.DefaultPageSize.Should().Be(UserLayoutPreferences.Default.DefaultPageSize);
        dto.DashboardWidgetOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ThenGet_RoundTripsTheJson()
    {
        // Arrange
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "layout-roundtrip",
            DisplayName = "Round-Trip",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var input = new UserLayoutPreferencesSaveDto(
            Grids: new Dictionary<string, GridLayoutDto>
            {
                ["solicitants"] = new(
                    VisibleColumns: ["name", "idnp"],
                    ColumnOrder: ["idnp", "name"],
                    PageSize: 50),
            },
            DefaultPageSize: 100,
            DashboardWidgetOrder: ["tasks", "applications"]);

        // Act
        var save = await harness.Service.SaveAsync(input);
        var read = await harness.Service.GetForCurrentUserAsync();

        // Assert
        save.IsSuccess.Should().BeTrue();
        save.Value!.DefaultPageSize.Should().Be(100);
        save.Value.Grids.Should().ContainKey("solicitants");

        read.DefaultPageSize.Should().Be(100);
        read.Grids.Should().ContainKey("solicitants");
        read.Grids["solicitants"].VisibleColumns.Should().Equal("name", "idnp");
        read.Grids["solicitants"].ColumnOrder.Should().Equal("idnp", "name");
        read.Grids["solicitants"].PageSize.Should().Be(50);
        read.DashboardWidgetOrder.Should().Equal("tasks", "applications");
    }

    [Fact]
    public async Task SaveAsync_EmitsInformationAuditWithCanonicalEventCode()
    {
        // Arrange
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "layout-audit",
            DisplayName = "Audit User",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var input = new UserLayoutPreferencesSaveDto(
            Grids: new Dictionary<string, GridLayoutDto>(),
            DefaultPageSize: 25,
            DashboardWidgetOrder: []);

        // Act
        var save = await harness.Service.SaveAsync(input);

        // Assert — Information severity, USER.LAYOUT.UPDATED event code, target = UserProfile.
        save.IsSuccess.Should().BeTrue();
        await harness.Audit.Received().RecordAsync(
            UserLayoutPreferencesService.AuditEventCode,
            AuditSeverity.Information,
            Arg.Any<string>(),
            nameof(UserProfile),
            profile.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetForCurrentUser_MalformedJson_FallsBackToDefaultsAndIncrementsCounter()
    {
        // Arrange — directly persist a malformed JSON payload.
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "layout-malformed",
            DisplayName = "Bad JSON User",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
            LayoutPreferences = "{ this is not valid json",
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        // Collect counter increments on cnas.user_layout.parse_failure across this test.
        long captured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CnasMeter.MeterName
                && string.Equals(instrument.Name, "cnas.user_layout.parse_failure", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref captured, value));
        listener.Start();

        // Act
        var dto = await harness.Service.GetForCurrentUserAsync();
        listener.RecordObservableInstruments();

        // Assert — fail-open: defaults; counter ticked.
        dto.DefaultPageSize.Should().Be(UserLayoutPreferences.Default.DefaultPageSize);
        dto.Grids.Should().BeEmpty();
        captured.Should().BeGreaterThanOrEqualTo(1L,
            "malformed JSON must increment cnas.user_layout.parse_failure (fail-open contract).");
    }

    [Fact]
    public async Task SaveAsync_AnonymousCaller_ReturnsUnauthorized()
    {
        var harness = Harness.Create(); // default caller has UserId=null
        var input = new UserLayoutPreferencesSaveDto(
            Grids: new Dictionary<string, GridLayoutDto>(),
            DefaultPageSize: 25,
            DashboardWidgetOrder: []);

        var save = await harness.Service.SaveAsync(input);

        save.IsFailure.Should().BeTrue();
        save.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task SaveAsync_DeactivatedUser_ReturnsNotFound()
    {
        // Arrange — the user row exists but IsActive=false (deactivated).
        var harness = Harness.Create();
        var profile = new UserProfile
        {
            MPassSubject = "layout-deactivated",
            DisplayName = "Deactivated",
            PreferredLanguage = "ro",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = false,
        };
        harness.Db.UserProfiles.Add(profile);
        await harness.Db.SaveChangesAsync();
        harness.AsCaller(profile.Id);

        var input = new UserLayoutPreferencesSaveDto(
            Grids: new Dictionary<string, GridLayoutDto>(),
            DefaultPageSize: 25,
            DashboardWidgetOrder: []);

        // Act
        var save = await harness.Service.SaveAsync(input);

        // Assert
        save.IsFailure.Should().BeTrue();
        save.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── Test harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-layout-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required UserLayoutPreferencesService Service { get; init; }
        public required ICallerContext Caller { get; init; }
        public required IAuditService Audit { get; init; }

        public void AsCaller(long userId)
        {
            Caller.UserId.Returns(userId);
            Caller.UserSqid.Returns($"sqid-{userId}");
        }

        public static Harness Create()
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns((long?)null);
            caller.Roles.Returns(Array.Empty<string>());
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-test");

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            var service = new UserLayoutPreferencesService(
                db, caller, clock, audit, NullLogger<UserLayoutPreferencesService>.Instance);

            return new Harness { Db = db, Service = service, Caller = caller, Audit = audit };
        }
    }
}
