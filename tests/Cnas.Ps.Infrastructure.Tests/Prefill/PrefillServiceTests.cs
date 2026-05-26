using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Prefill;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Prefill;

/// <summary>
/// R0552 / R0562 / TOR CF 06.03 + CF 07.03 — service-layer tests covering the
/// pre-fill merge / conflict / timeout / permission / audit logic.
/// </summary>
public sealed class PrefillServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>1. Happy path — all three sources return data, payload assembled, no warnings.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_AllSourcesReturnData_PayloadAssembled()
    {
        await using var h = await Harness.CreateAsync();
        var solicitantId = h.SeedSolicitantForCaller();
        h.Rsp.SetField(PrefillFields.FullName, "ANA POPESCU");
        h.SiSfs.SetField(PrefillFields.Email, "ana@example.md");
        h.Rsud.SetField(PrefillFields.City, "Chisinau");

        var res = await h.Service.PrefillForCurrentUserAsync(new PrefillRequestDto(null, null));

        res.IsSuccess.Should().BeTrue();
        res.Value.Fields.Should().ContainKey(PrefillFields.FullName);
        res.Value.Fields[PrefillFields.FullName].Value.Should().Be("ANA POPESCU");
        res.Value.Fields[PrefillFields.FullName].Source.Should().Be(PrefillSources.Rsp);
        res.Value.Fields.Should().ContainKey(PrefillFields.Email);
        res.Value.Fields.Should().ContainKey(PrefillFields.City);
        res.Value.Warnings.Should().BeEmpty();
        res.Value.SolicitantSqid.Should().NotBeNullOrEmpty();
    }

    /// <summary>2. Source priority — RSP and RSUD both return Address; RSP wins.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_RspAndRsudBothReturnAddress_RspWins()
    {
        await using var h = await Harness.CreateAsync();
        h.SeedSolicitantForCaller();
        h.Rsp.SetField(PrefillFields.Address, "RSP-ADDR");
        h.Rsud.SetField(PrefillFields.Address, "RSUD-ADDR");

        var res = await h.Service.PrefillForCurrentUserAsync(new PrefillRequestDto(null, null));

        res.IsSuccess.Should().BeTrue();
        res.Value.Fields[PrefillFields.Address].Value.Should().Be("RSP-ADDR");
        res.Value.Fields[PrefillFields.Address].Source.Should().Be(PrefillSources.Rsp);
        res.Value.SourceUsedPerField[PrefillFields.Address].Should().Be(PrefillSources.Rsp);
    }

    /// <summary>3. Warning emitted on conflict including the discarded value.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_ConflictBetweenSources_WarningCarriesDiscardedValue()
    {
        await using var h = await Harness.CreateAsync();
        h.SeedSolicitantForCaller();
        h.Rsp.SetField(PrefillFields.Address, "RSP-ADDR");
        h.Rsud.SetField(PrefillFields.Address, "RSUD-ADDR");

        var res = await h.Service.PrefillForCurrentUserAsync(new PrefillRequestDto(null, null));

        res.IsSuccess.Should().BeTrue();
        res.Value.Warnings.Should().ContainSingle(w =>
            w.Contains(PrefillFields.Address, StringComparison.Ordinal)
            && w.Contains("RSP", StringComparison.Ordinal)
            && w.Contains("RSUD", StringComparison.Ordinal));
    }

    /// <summary>4. Source filtering — only RSP requested, RSUD/SI_SFS not queried.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_SourcesRspOnly_OnlyRspQueried()
    {
        await using var h = await Harness.CreateAsync();
        h.SeedSolicitantForCaller();
        h.Rsp.SetField(PrefillFields.FullName, "RSP-NAME");
        h.SiSfs.SetField(PrefillFields.Email, "should-not-appear@example.md");
        h.Rsud.SetField(PrefillFields.City, "RSUD-CITY");

        var res = await h.Service.PrefillForCurrentUserAsync(
            new PrefillRequestDto(new[] { PrefillSources.Rsp }, null));

        res.IsSuccess.Should().BeTrue();
        res.Value.Fields.Should().ContainKey(PrefillFields.FullName);
        res.Value.Fields.Should().NotContainKey(PrefillFields.Email);
        res.Value.Fields.Should().NotContainKey(PrefillFields.City);
        h.SiSfs.CallCount.Should().Be(0);
        h.Rsud.CallCount.Should().Be(0);
        h.Rsp.CallCount.Should().Be(1);
    }

    /// <summary>5. Field filtering — only FullName and Email requested.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_FieldsAllowList_OnlyThoseFieldsReturned()
    {
        await using var h = await Harness.CreateAsync();
        h.SeedSolicitantForCaller();
        h.Rsp.SetField(PrefillFields.FullName, "ANA");
        h.Rsp.SetField(PrefillFields.City, "EXCLUDED");
        h.SiSfs.SetField(PrefillFields.Email, "ana@example.md");
        h.SiSfs.SetField(PrefillFields.Phone, "EXCLUDED-PHONE");

        var res = await h.Service.PrefillForCurrentUserAsync(
            new PrefillRequestDto(null, new[] { PrefillFields.FullName, PrefillFields.Email }));

        res.IsSuccess.Should().BeTrue();
        res.Value.Fields.Keys.Should().BeEquivalentTo(new[] { PrefillFields.FullName, PrefillFields.Email });
    }

    /// <summary>6. Field outside source allow-list — RSUD asked for FullName, warning emitted, no value.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_FieldOutsideSourceAllowList_WarningEmitted()
    {
        await using var h = await Harness.CreateAsync();
        h.SeedSolicitantForCaller();
        // RSUD does not carry FullName; the request must still succeed but warn.
        h.Rsud.SetField(PrefillFields.FullName, "should-be-ignored");

        var res = await h.Service.PrefillForCurrentUserAsync(
            new PrefillRequestDto(new[] { PrefillSources.Rsud }, new[] { PrefillFields.FullName }));

        res.IsSuccess.Should().BeTrue();
        res.Value.Fields.Should().NotContainKey(PrefillFields.FullName);
        res.Value.Warnings.Should().ContainSingle(w =>
            w.Contains(PrefillFields.FullName, StringComparison.Ordinal)
            && w.Contains(PrefillSources.Rsud, StringComparison.Ordinal));
    }

    /// <summary>7. RSP gateway times out (TaskCanceledException) — warning emitted, others succeed.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_RspTimesOut_WarningEmitted_OthersStillMerged()
    {
        await using var h = await Harness.CreateAsync();
        h.SeedSolicitantForCaller();
        h.Rsp.ThrowOnFetch = new TaskCanceledException("RSP took too long");
        h.SiSfs.SetField(PrefillFields.Email, "ana@example.md");
        h.Rsud.SetField(PrefillFields.City, "Chisinau");

        var res = await h.Service.PrefillForCurrentUserAsync(new PrefillRequestDto(null, null));

        res.IsSuccess.Should().BeTrue();
        res.Value.Warnings.Should().Contain(w => w.Contains("RSP", StringComparison.Ordinal));
        res.Value.Fields.Should().ContainKey(PrefillFields.Email);
        res.Value.Fields.Should().ContainKey(PrefillFields.City);
    }

    /// <summary>8. PrefillForSolicitantAsync without permission → Forbidden.</summary>
    [Fact]
    public async Task R0562_PrefillForSolicitantAsync_CallerLacksPermission_ReturnsForbidden()
    {
        await using var h = await Harness.CreateAsync();
        var solicitantId = h.SeedSolicitantForCaller();
        // The default StubCaller has no Prefill.ForAnyApplicant permission.

        var res = await h.Service.PrefillForSolicitantAsync(
            solicitantId, new PrefillRequestDto(null, null));

        res.IsFailure.Should().BeTrue();
        res.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    /// <summary>9. PrefillForSolicitantAsync with permission returns the requested solicitant's data.</summary>
    [Fact]
    public async Task R0562_PrefillForSolicitantAsync_WithPermission_ReturnsTargetData()
    {
        await using var h = await Harness.CreateAsync();
        var solicitantId = h.SeedAnotherSolicitant("2999900000007");
        h.Caller.GrantPermission(IPrefillService.ForAnyApplicantPermission);
        h.Rsp.SetField(PrefillFields.FullName, "TARGET CITIZEN");

        var res = await h.Service.PrefillForSolicitantAsync(
            solicitantId, new PrefillRequestDto(null, null));

        res.IsSuccess.Should().BeTrue();
        res.Value.SolicitantSqid.Should().Be(h.Sqids.Encode(solicitantId));
        res.Value.Fields[PrefillFields.FullName].Value.Should().Be("TARGET CITIZEN");
    }

    /// <summary>10. Audit Sensitive PREFILL.RETRIEVED row written; includes sourcesUsed array.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_OnSuccess_EmitsSensitiveAuditWithSourcesUsed()
    {
        await using var h = await Harness.CreateAsync();
        h.SeedSolicitantForCaller();
        h.Rsp.SetField(PrefillFields.FullName, "ANA");

        await h.Service.PrefillForCurrentUserAsync(new PrefillRequestDto(null, null));

        h.Audit.Events.Should().Contain(e =>
            e.EventCode == "PREFILL.RETRIEVED" && e.Severity == AuditSeverity.Sensitive);
        var detail = h.Audit.Events.Single(e => e.EventCode == "PREFILL.RETRIEVED").DetailsJson;
        detail.Should().Contain("sourcesUsed", "audit detail should enumerate queried sources");
    }

    /// <summary>14. Empty Sources defaults to all 3 — confirm SI_SFS/RSUD both called.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_EmptySourcesArray_DefaultsToAllThree()
    {
        await using var h = await Harness.CreateAsync();
        h.SeedSolicitantForCaller();

        await h.Service.PrefillForCurrentUserAsync(
            new PrefillRequestDto(Array.Empty<string>(), null));

        h.Rsp.CallCount.Should().Be(1);
        h.Rsud.CallCount.Should().Be(1);
        h.SiSfs.CallCount.Should().Be(1);
    }

    /// <summary>
    /// Bonus — unauthenticated caller surfaces Unauthorized (defense in depth — the
    /// controller's [Authorize] is the primary gate, but the service re-checks).
    /// </summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_AnonymousCaller_ReturnsUnauthorized()
    {
        await using var h = await Harness.CreateAsync();
        h.Caller.SignOut();

        var res = await h.Service.PrefillForCurrentUserAsync(new PrefillRequestDto(null, null));

        res.IsFailure.Should().BeTrue();
        res.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    /// <summary>Bonus — when no Solicitant is linked to the caller, returns NotFound.</summary>
    [Fact]
    public async Task R0552_PrefillForCurrentUserAsync_NoLinkedSolicitant_ReturnsNotFound()
    {
        await using var h = await Harness.CreateAsync();
        // Seed a UserProfile but NO Solicitant.
        h.SeedUserProfileOnly();

        var res = await h.Service.PrefillForCurrentUserAsync(new PrefillRequestDto(null, null));

        res.IsFailure.Should().BeTrue();
        res.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─── helpers ─────────────────────

    private sealed class StubClock : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = ClockNow;
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

    private sealed class MutableCaller : ICallerContext
    {
        private readonly HashSet<string> _roles = new(StringComparer.Ordinal);
        private long? _userId = 42L;
        private string? _userSqid = "user-sqid";

        public void GrantPermission(string perm) => _roles.Add(perm);
        public void SignOut() { _userId = null; _userSqid = null; }

        /// <inheritdoc />
        public long? UserId => _userId;
        /// <inheritdoc />
        public string? UserSqid => _userSqid;
        /// <inheritdoc />
        public IReadOnlyCollection<string> Roles => _roles;
        /// <inheritdoc />
        public string? SourceIp => "127.0.0.1";
        /// <inheritdoc />
        public string? CorrelationId => "corr-prefill";
        /// <inheritdoc />
        public string? OnBehalfOfPrincipalIdnp => null;
        /// <inheritdoc />
        public string? DelegationPowerId => null;
        /// <inheritdoc />
        public IAccessScope AccessScope => RolesBasedAccessScope.Unscoped;
        /// <inheritdoc />
        public string? SessionId => null;
    }

    /// <summary>Fake gateway that returns one canned value per registered field.</summary>
    private sealed class FakePrefillGateway
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Exception? ThrowOnFetch { get; set; }
        public int CallCount { get; private set; }

        public IReadOnlyDictionary<string, string> Values => _values;

        public void SetField(string fieldName, string value) => _values[fieldName] = value;

        public Task<IReadOnlyDictionary<string, string>> FetchAsync(string idnp, CancellationToken ct)
        {
            CallCount++;
            if (ThrowOnFetch is not null)
            {
                throw ThrowOnFetch;
            }
            return Task.FromResult<IReadOnlyDictionary<string, string>>(_values);
        }
    }

    /// <summary>Test-only adapter that fronts a <see cref="FakePrefillGateway"/> as an <see cref="IRspGateway"/>.</summary>
    private sealed class FakeRspAdapter(FakePrefillGateway inner) : IRspGateway, IPrefillSourceAdapter
    {
        public string SourceCode => PrefillSources.Rsp;
        public Task<IReadOnlyDictionary<string, string>> FetchPrefillAsync(string idnp, CancellationToken ct)
            => inner.FetchAsync(idnp, ct);
        public Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Success(Array.Empty<ProfileRefreshDeltaDto>()));
    }

    private sealed class FakeRsudAdapter(FakePrefillGateway inner) : IRsudGateway, IPrefillSourceAdapter
    {
        public string SourceCode => PrefillSources.Rsud;
        public Task<IReadOnlyDictionary<string, string>> FetchPrefillAsync(string idnp, CancellationToken ct)
            => inner.FetchAsync(idnp, ct);
        public Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Success(Array.Empty<ProfileRefreshDeltaDto>()));
    }

    private sealed class FakeSiSfsAdapter(FakePrefillGateway inner) : ISiSfsGateway, IPrefillSourceAdapter
    {
        public string SourceCode => PrefillSources.SiSfs;
        public Task<IReadOnlyDictionary<string, string>> FetchPrefillAsync(string idnp, CancellationToken ct)
            => inner.FetchAsync(idnp, ct);
        public Task<Result<IReadOnlyList<ProfileRefreshDeltaDto>>> FetchDeltasAsync(string idnp, CancellationToken ct = default)
            => Task.FromResult(Result<IReadOnlyList<ProfileRefreshDeltaDto>>.Success(Array.Empty<ProfileRefreshDeltaDto>()));
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required CnasDbContext Db { get; init; }
        public required PrefillService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required MutableCaller Caller { get; init; }
        public required FakePrefillGateway Rsp { get; init; }
        public required FakePrefillGateway Rsud { get; init; }
        public required FakePrefillGateway SiSfs { get; init; }

        private const string CallerIdnp = "1003600054321";

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-prefill-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var clock = new StubClock();
            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var audit = new RecordingAudit();
            var caller = new MutableCaller();
            var rsp = new FakePrefillGateway();
            var rsud = new FakePrefillGateway();
            var siSfs = new FakePrefillGateway();
            var service = new PrefillService(
                db,
                new FakeRspAdapter(rsp),
                new FakeRsudAdapter(rsud),
                new FakeSiSfsAdapter(siSfs),
                clock, sqids, caller, audit,
                NullLogger<PrefillService>.Instance);
            return Task.FromResult(new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
                Audit = audit,
                Caller = caller,
                Rsp = rsp,
                Rsud = rsud,
                SiSfs = siSfs,
            });
        }

        /// <summary>
        /// Seeds a UserProfile (id = caller.UserId) AND a Solicitant matched on the
        /// same NationalIdHash, so PrefillForCurrentUserAsync can resolve it.
        /// </summary>
        public long SeedSolicitantForCaller()
        {
            var hash = IdHashHelper.Hash(CallerIdnp);
            Db.UserProfiles.Add(new UserProfile
            {
                Id = 42L,
                DisplayName = "Ana Popescu",
                NationalId = CallerIdnp,
                NationalIdHash = hash,
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            });
            var s = new Solicitant
            {
                NationalId = CallerIdnp,
                NationalIdHash = hash,
                DisplayName = "Ana Popescu",
                Kind = ApplicantKind.NaturalPerson,
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.Solicitants.Add(s);
            Db.SaveChanges();
            return s.Id;
        }

        /// <summary>Seeds an arbitrary other Solicitant (used by R0562 tests).</summary>
        public long SeedAnotherSolicitant(string idnp)
        {
            var s = new Solicitant
            {
                NationalId = idnp,
                NationalIdHash = IdHashHelper.Hash(idnp),
                DisplayName = "Other Citizen",
                Kind = ApplicantKind.NaturalPerson,
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.Solicitants.Add(s);
            Db.SaveChanges();
            return s.Id;
        }

        /// <summary>Seeds a UserProfile for the caller but NO Solicitant link.</summary>
        public void SeedUserProfileOnly()
        {
            var hash = IdHashHelper.Hash(CallerIdnp);
            Db.UserProfiles.Add(new UserProfile
            {
                Id = 42L,
                DisplayName = "Ana Popescu",
                NationalId = CallerIdnp,
                NationalIdHash = hash,
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            });
            Db.SaveChanges();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync() => await Db.DisposeAsync().ConfigureAwait(false);
    }
}
