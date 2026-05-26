using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0363 / TOR UC13 strategy 3 — tests covering external-data refresh outcomes:
/// success, no-change, partial-failure, unknown source, and audit emission.
/// </summary>
public sealed class ProfileRefreshServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 11, 0, 0, DateTimeKind.Utc);

    /// <summary>RSP returns 2 deltas → both apply → <c>Success</c> + audit row emitted.</summary>
    [Fact]
    public async Task RefreshFromSourceAsync_TwoDeltasApplied_OutcomeSuccess()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        h.Rsp.Deltas = new List<ProfileRefreshDeltaDto>
        {
            BuildAddressDelta(),
            BuildContactDelta(),
        };

        var res = await h.Service.RefreshFromSourceAsync(ProfileRefreshService.SourceRsp, contributorId);

        res.IsSuccess.Should().BeTrue();
        res.Value.RowsApplied.Should().Be(2);
        res.Value.Outcome.Should().Be(nameof(ProfileRefreshOutcome.Success));
        (await h.Db.ProfileRefreshRuns.CountAsync()).Should().Be(1);
        h.Audit.Events.Should().Contain(e =>
            e.EventCode == "PROFILE.REFRESH.COMPLETED" && e.Severity == AuditSeverity.Sensitive);
    }

    /// <summary>Empty delta set → outcome <c>NoChange</c>; <c>RowsApplied=0</c>.</summary>
    [Fact]
    public async Task RefreshFromSourceAsync_EmptyDeltas_OutcomeNoChange()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        h.Rsp.Deltas = new List<ProfileRefreshDeltaDto>();

        var res = await h.Service.RefreshFromSourceAsync(ProfileRefreshService.SourceRsp, contributorId);

        res.IsSuccess.Should().BeTrue();
        res.Value.RowsApplied.Should().Be(0);
        res.Value.Outcome.Should().Be(nameof(ProfileRefreshOutcome.NoChange));
    }

    /// <summary>One valid + one bogus delta → <c>PartialFailure</c> with split counters.</summary>
    [Fact]
    public async Task RefreshFromSourceAsync_OneAppliedOneFailed_OutcomePartialFailure()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        h.Rsp.Deltas = new List<ProfileRefreshDeltaDto>
        {
            BuildAddressDelta(),
            // Bogus civil status — UpdateCivilStatusAsync rejects it with ValidationFailed.
            new ProfileRefreshDeltaDto(
                ChildEntityType: "CivilStatus",
                FieldName: "status",
                OldValue: null,
                NewValue: "Cohabiting",
                PayloadJson: "{\"status\":\"Cohabiting\",\"effectiveDate\":null}"),
        };

        var res = await h.Service.RefreshFromSourceAsync(ProfileRefreshService.SourceRsp, contributorId);

        res.IsSuccess.Should().BeTrue();
        res.Value.Outcome.Should().Be(nameof(ProfileRefreshOutcome.PartialFailure));
        res.Value.RowsApplied.Should().Be(1);
        res.Value.RowsSkipped.Should().Be(1);
        res.Value.FailureSummary.Should().NotBeNullOrEmpty();
    }

    /// <summary>Unknown source → <c>PROFILE_REFRESH_UNKNOWN_SOURCE</c>; no row written.</summary>
    [Fact]
    public async Task RefreshFromSourceAsync_UnknownSource_FailsWithStableCode()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();

        var res = await h.Service.RefreshFromSourceAsync("FAKE", contributorId);

        res.IsFailure.Should().BeTrue();
        res.ErrorCode.Should().Be(ErrorCodes.ProfileRefreshUnknownSource);
        (await h.Db.ProfileRefreshRuns.CountAsync()).Should().Be(0);
    }

    /// <summary><c>ListRecentAsync</c> orders rows by <c>StartedUtc DESC</c>.</summary>
    [Fact]
    public async Task ListRecentAsync_ReturnsRowsNewestFirst()
    {
        await using var h = await Harness.CreateAsync();
        var contributorId = h.SeedContributor();
        h.Rsp.Deltas = new List<ProfileRefreshDeltaDto>(); // NoChange runs
        await h.Service.RefreshFromSourceAsync(ProfileRefreshService.SourceRsp, contributorId);
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await h.Service.RefreshFromSourceAsync(ProfileRefreshService.SourceRsp, contributorId);

        var list = await h.Service.ListRecentAsync(10);

        list.IsSuccess.Should().BeTrue();
        list.Value.Should().HaveCount(2);
        list.Value[0].StartedUtc.Should().BeAfter(list.Value[1].StartedUtc);
    }

    // ─── helpers ─────────────────────

    /// <summary>Sample Address delta carrying a writer-compatible payload.</summary>
    private static ProfileRefreshDeltaDto BuildAddressDelta() => new(
        ChildEntityType: "Address",
        FieldName: "street",
        OldValue: null,
        NewValue: "Strada Stefan cel Mare 1",
        PayloadJson: JsonSerializer.Serialize(new
        {
            street = "Strada Stefan cel Mare 1",
            city = "Chisinau",
            region = "Chisinau",
            postalCode = "MD2001",
            country = "MD",
        }));

    /// <summary>Sample Contact delta carrying a writer-compatible payload.</summary>
    private static ProfileRefreshDeltaDto BuildContactDelta() => new(
        ChildEntityType: "Contact",
        FieldName: "phone",
        OldValue: null,
        NewValue: "+37312345678",
        PayloadJson: JsonSerializer.Serialize(new
        {
            phoneE164 = "+37312345678",
            email = (string?)null,
            contactPersonName = (string?)null,
        }));

    private sealed class StubClock : ICnasTimeProvider
    {
        private DateTime _now;
        public StubClock(DateTime now) { _now = now; }
        /// <inheritdoc />
        public DateTime UtcNow => _now;
        /// <summary>Advances the clock by <paramref name="span"/> for tests that need ordered rows.</summary>
        public void Advance(TimeSpan span) => _now += span;
    }

    private sealed class RecordingAudit : IAuditService
    {
        public List<(string EventCode, AuditSeverity Severity, string DetailsJson)> Events { get; } = new();
        /// <inheritdoc />
        public Task<Result> RecordAsync(string eventCode, AuditSeverity severity, string actorId,
            string? targetEntity, long? targetEntityId, string detailsJson, string? sourceIp,
            string? correlationId, CancellationToken cancellationToken = default)
        {
            Events.Add((eventCode, severity, detailsJson));
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class StubCaller : ICallerContext
    {
        /// <inheritdoc />
        public long? UserId => 1;
        /// <inheritdoc />
        public string? UserSqid => "user-sqid";
        /// <inheritdoc />
        public IReadOnlyCollection<string> Roles => new[] { "cnas-admin" };
        /// <inheritdoc />
        public string? SourceIp => "127.0.0.1";
        /// <inheritdoc />
        public string? CorrelationId => "corr-1";
        /// <inheritdoc />
        public string? OnBehalfOfPrincipalIdnp => null;
        /// <inheritdoc />
        public string? DelegationPowerId => null;
        /// <inheritdoc />
        public IAccessScope AccessScope => RolesBasedAccessScope.Unscoped;
        /// <inheritdoc />
        public string? SessionId => null;
    }

    private sealed class FakeRspGateway : IRspGateway
    {
        public IReadOnlyList<ProfileRefreshDeltaDto> Deltas { get; set; } = new List<ProfileRefreshDeltaDto>();
        /// <inheritdoc />
        public Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Success(Deltas));
    }

    private sealed class FakeRsudGateway : IRsudGateway
    {
        /// <inheritdoc />
        public Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Success(
                (IReadOnlyList<ProfileRefreshDeltaDto>)new List<ProfileRefreshDeltaDto>()));
    }

    private sealed class FakeSiSfsGateway : ISiSfsGateway
    {
        /// <inheritdoc />
        public Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Success(
                (IReadOnlyList<ProfileRefreshDeltaDto>)new List<ProfileRefreshDeltaDto>()));
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required CnasDbContext Db { get; init; }
        public required ProfileRefreshService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required StubClock Clock { get; init; }
        public required FakeRspGateway Rsp { get; init; }

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-profile-refresh-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var clock = new StubClock(ClockNow);
            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var audit = new RecordingAudit();
            var caller = new StubCaller();
            var rsp = new FakeRspGateway();
            var rsud = new FakeRsudGateway();
            var siSfs = new FakeSiSfsGateway();
            var contributorWriter = new ContributorLinkedEntitiesService(db, clock, sqids, caller, audit);
            var service = new ProfileRefreshService(
                db, contributorWriter, rsp, rsud, siSfs, clock, sqids, caller, audit,
                NullLogger<ProfileRefreshService>.Instance);
            return Task.FromResult(new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
                Audit = audit,
                Clock = clock,
                Rsp = rsp,
            });
        }

        public long SeedContributor()
        {
            var c = new InsuredPerson
            {
                Idnp = "1003600012346",
                IdnpHash = "hash-seed",
                FirstName = "Ana",
                LastName = "Popescu",
                BirthDate = new DateOnly(1990, 5, 10),
                RegisteredAtUtc = ClockNow.AddDays(-30),
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.InsuredPersons.Add(c);
            Db.SaveChanges();
            return c.Id;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync() => await Db.DisposeAsync().ConfigureAwait(false);
    }
}
