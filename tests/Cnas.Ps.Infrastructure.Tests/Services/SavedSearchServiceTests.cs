using System.Diagnostics.Metrics;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
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
/// R0165 / CF 03.06 — service-level tests for <see cref="SavedSearchService"/>. Uses EF
/// Core InMemory + NSubstitute, mirroring the harness shape in
/// <see cref="PendingAdminActionServiceTests"/>. Each test exercises one branch of the
/// CRUD / sharing / validation matrix.
/// </summary>
/// <remarks>
/// Member of <see cref="CnasMeterCollection"/> because the create/update happy paths
/// emit on <see cref="CnasMeter.SavedSearchSaved"/>; cross-class parallelism on the
/// process-static meter would inflate the "exactly N increments" assertion.
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public class SavedSearchServiceTests
{
    /// <summary>Deterministic clock anchor for all tests.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Default registry code used by tests that don't care which registry.</summary>
    private const string Registry = "Contributors";

    // ─────────────────────── CreateAsync ───────────────────────

    [Fact]
    public async Task Create_ValidInput_PersistsRow_AndReturnsSqid()
    {
        var harness = Harness.Create();

        var result = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry: Registry,
            Name: "My contributors",
            FilterJson: "{\"city\":\"Chișinău\"}",
            IsShared: false));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().StartWith("SQID-");

        var row = await harness.Db.SavedSearches.SingleAsync();
        row.OwnerUserId.Should().Be(Harness.OwnerUserId);
        row.Registry.Should().Be(Registry);
        row.Name.Should().Be("My contributors");
        row.FilterJson.Should().Be("{\"city\":\"Chișinău\"}");
        row.IsShared.Should().BeFalse();
        row.IsActive.Should().BeTrue();
        row.CreatedAtUtc.Should().Be(ClockNow);
    }

    [Fact]
    public async Task Create_DuplicateNameOnSameRegistry_IsIdempotent_ReturnsExistingSqid()
    {
        var harness = Harness.Create();
        var first = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "My contributors", "{}", IsShared: false));
        first.IsSuccess.Should().BeTrue();

        // Second create on the same triple — must NOT insert a duplicate; instead it
        // returns the existing row's Sqid verbatim. This is the idempotent-create
        // contract documented on ISavedSearchService.CreateAsync.
        var second = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "My contributors", "{\"different\":\"payload\"}", IsShared: true));

        second.IsSuccess.Should().BeTrue();
        second.Value.Should().Be(first.Value);

        // Only one row exists; its fields are the FIRST create's payload (idempotent
        // means "no overwrite", not "merge").
        var rows = await harness.Db.SavedSearches.ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].FilterJson.Should().Be("{}");
        rows[0].IsShared.Should().BeFalse();
    }

    [Fact]
    public async Task Create_PerOwnerLimitExceeded_ReturnsLimitReached()
    {
        // Tight cap so the test stays fast.
        var harness = Harness.Create(opts: new SavedSearchOptions { MaxPerOwner = 2, MaxFilterJsonLength = 8192, MaxNameLength = 128 });
        (await harness.Service.CreateAsync(new SavedSearchCreateInput(Registry, "n1", "{}", false))).IsSuccess.Should().BeTrue();
        (await harness.Service.CreateAsync(new SavedSearchCreateInput(Registry, "n2", "{}", false))).IsSuccess.Should().BeTrue();

        var overflow = await harness.Service.CreateAsync(new SavedSearchCreateInput(Registry, "n3", "{}", false));

        overflow.IsFailure.Should().BeTrue();
        overflow.ErrorCode.Should().Be(ErrorCodes.SavedSearchLimitReached);
        (await harness.Db.SavedSearches.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Create_OversizedFilterJson_ReturnsValidationFailed()
    {
        var harness = Harness.Create(opts: new SavedSearchOptions { MaxPerOwner = 50, MaxFilterJsonLength = 32, MaxNameLength = 128 });

        var result = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", new string('x', 64), IsShared: false));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await harness.Db.SavedSearches.CountAsync()).Should().Be(0);
    }

    // ─────────────────────── UpdateAsync ───────────────────────

    [Fact]
    public async Task Update_AsOwner_ApplyChanges()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", "{}", IsShared: false));
        create.IsSuccess.Should().BeTrue();

        var update = await harness.Service.UpdateAsync(create.Value, new SavedSearchUpdateInput(
            Name: "n1 (renamed)", FilterJson: "{\"new\":true}", IsShared: true));

        update.IsSuccess.Should().BeTrue();
        var row = await harness.Db.SavedSearches.SingleAsync();
        row.Name.Should().Be("n1 (renamed)");
        row.FilterJson.Should().Be("{\"new\":true}");
        row.IsShared.Should().BeTrue();
    }

    [Fact]
    public async Task Update_AsNonOwner_ReturnsForbidden()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", "{}", IsShared: true));
        create.IsSuccess.Should().BeTrue();

        // Switch caller to a different user — they have READ access to the shared row
        // but updating it must fail with Forbidden.
        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");
        var update = await otherHarness.Service.UpdateAsync(create.Value, new SavedSearchUpdateInput(
            "n1-stealth", "{}", IsShared: false));

        update.IsFailure.Should().BeTrue();
        update.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        // Original fields untouched.
        var row = await harness.Db.SavedSearches.SingleAsync();
        row.Name.Should().Be("n1");
        row.IsShared.Should().BeTrue();
    }

    // ─────────────────────── DeleteAsync ───────────────────────

    [Fact]
    public async Task Delete_AsOwner_SoftDeletesRow()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", "{}", IsShared: false));
        create.IsSuccess.Should().BeTrue();

        var del = await harness.Service.DeleteAsync(create.Value);

        del.IsSuccess.Should().BeTrue();
        // Row remains in the table but IsActive flipped to false (soft delete contract).
        var row = await harness.Db.SavedSearches.IgnoreQueryFilters().SingleAsync();
        row.IsActive.Should().BeFalse();
        // Listing now returns nothing because the row is excluded.
        var list = await harness.Service.ListAsync(Registry);
        list.IsSuccess.Should().BeTrue();
        list.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_AsNonOwner_ReturnsForbidden()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", "{}", IsShared: true));
        create.IsSuccess.Should().BeTrue();

        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");
        var del = await otherHarness.Service.DeleteAsync(create.Value);

        del.IsFailure.Should().BeTrue();
        del.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        var row = await harness.Db.SavedSearches.SingleAsync();
        row.IsActive.Should().BeTrue();
    }

    // ─────────────────────── GetAsync access rules ───────────────────────

    [Fact]
    public async Task Get_OwnRow_AlwaysReturnsRow_RegardlessOfIsShared()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", "{}", IsShared: false));

        var get = await harness.Service.GetAsync(create.Value);

        get.IsSuccess.Should().BeTrue();
        get.Value.Name.Should().Be("n1");
        get.Value.OwnerUserId.Should().StartWith("SQID-");
    }

    [Fact]
    public async Task Get_SharedRow_AsNonOwner_ReturnsRow()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "shared", "{}", IsShared: true));

        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");
        var get = await otherHarness.Service.GetAsync(create.Value);

        get.IsSuccess.Should().BeTrue();
        get.Value.IsShared.Should().BeTrue();
        get.Value.Name.Should().Be("shared");
    }

    [Fact]
    public async Task Get_PrivateRow_AsNonOwner_ReturnsForbidden()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "private", "{}", IsShared: false));

        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");
        var get = await otherHarness.Service.GetAsync(create.Value);

        get.IsFailure.Should().BeTrue();
        get.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    // ─────────────────────── ListAsync union behaviour ───────────────────────

    [Fact]
    public async Task List_ReturnsOnlyOwnedRows_NoSharedFromOthers()
    {
        // R0524: ListAsync (a.k.a. "list mine") returns ONLY caller-owned rows. The
        // broader union (owned + Shared + Group-where-member) lives on
        // ListAccessibleAsync — covered separately below.
        var harness = Harness.Create();
        // Owner has one private + one shared row on Contributors.
        (await harness.Service.CreateAsync(new SavedSearchCreateInput(Registry, "owner-private", "{}", false))).IsSuccess.Should().BeTrue();
        (await harness.Service.CreateAsync(new SavedSearchCreateInput(Registry, "owner-shared", "{}", true))).IsSuccess.Should().BeTrue();
        // A different user owns one shared row on Contributors — it must NOT appear in
        // the owner's "ListAsync" (only "ListAccessibleAsync" returns the union).
        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");
        (await otherHarness.Service.CreateAsync(new SavedSearchCreateInput(Registry, "other-shared", "{}", true))).IsSuccess.Should().BeTrue();

        var list = await harness.Service.ListAsync(Registry);

        list.IsSuccess.Should().BeTrue();
        list.Value.Select(i => i.Name).Should().BeEquivalentTo(["owner-private", "owner-shared"]);
    }

    // ─────────────────────── ShareAsync ───────────────────────

    [Fact]
    public async Task Share_FlipToShared_PersistsScope_AndEmitsAudit()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", "{}", IsShared: false));
        create.IsSuccess.Should().BeTrue();

        var share = await harness.Service.ShareAsync(create.Value, new SavedSearchShareInput(
            SharingScope: nameof(SavedSearchSharingScope.Shared), SharedWithGroupCode: null));

        share.IsSuccess.Should().BeTrue();
        share.Value.SharingScope.Should().Be(nameof(SavedSearchSharingScope.Shared));
        share.Value.SharedWithGroupCode.Should().BeNull();
        share.Value.IsShared.Should().BeTrue();

        var row = await harness.Db.SavedSearches.SingleAsync();
        row.SharingScope.Should().Be(SavedSearchSharingScope.Shared);
        row.SharedWithGroupCode.Should().BeNull();
        // Legacy IsShared mirror stays in sync.
        row.IsShared.Should().BeTrue();

        // Notice-severity audit row written with the SAVED_SEARCH.SHARED code and
        // a JSON payload describing the scope.
        await harness.Audit.Received(1).RecordAsync(
            "SAVED_SEARCH.SHARED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(SavedSearch),
            row.Id,
            Arg.Is<string>(json => json.Contains("Shared", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Share_FlipToGroup_PersistsScopeAndGroupCode()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", "{}", IsShared: false));
        create.IsSuccess.Should().BeTrue();

        var share = await harness.Service.ShareAsync(create.Value, new SavedSearchShareInput(
            SharingScope: nameof(SavedSearchSharingScope.Group),
            SharedWithGroupCode: "pensions.examiners"));

        share.IsSuccess.Should().BeTrue();
        share.Value.SharingScope.Should().Be(nameof(SavedSearchSharingScope.Group));
        share.Value.SharedWithGroupCode.Should().Be("pensions.examiners");
        // Group scope does NOT set legacy IsShared (only the unilateral Shared scope does).
        share.Value.IsShared.Should().BeFalse();

        var row = await harness.Db.SavedSearches.SingleAsync();
        row.SharingScope.Should().Be(SavedSearchSharingScope.Group);
        row.SharedWithGroupCode.Should().Be("pensions.examiners");
        row.IsShared.Should().BeFalse();
    }

    [Fact]
    public async Task Share_AsNonOwner_ReturnsForbidden()
    {
        var harness = Harness.Create();
        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", "{}", IsShared: true));
        create.IsSuccess.Should().BeTrue();

        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");
        var share = await otherHarness.Service.ShareAsync(create.Value, new SavedSearchShareInput(
            SharingScope: nameof(SavedSearchSharingScope.Private), SharedWithGroupCode: null));

        share.IsFailure.Should().BeTrue();
        share.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        // Scope was NOT mutated by the rejected request.
        var row = await harness.Db.SavedSearches.SingleAsync();
        row.SharingScope.Should().Be(SavedSearchSharingScope.Shared);
    }

    // ─────────────────────── ListAccessibleAsync ───────────────────────

    [Fact]
    public async Task ListAccessible_ReturnsOwnedRows()
    {
        var harness = Harness.Create();
        await SeedUserProfileAsync(harness.Db, Harness.OwnerUserId, []);
        (await harness.Service.CreateAsync(new SavedSearchCreateInput(Registry, "mine", "{}", false))).IsSuccess.Should().BeTrue();

        var list = await harness.Service.ListAccessibleAsync(Registry);

        list.Should().HaveCount(1);
        list[0].Name.Should().Be("mine");
    }

    [Fact]
    public async Task ListAccessible_ReturnsSharedRowsOfOtherOwners()
    {
        var harness = Harness.Create();
        await SeedUserProfileAsync(harness.Db, Harness.OwnerUserId, []);
        // Another user owns a Shared row.
        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");
        var create = await otherHarness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "other-shared", "{}", IsShared: false));
        create.IsSuccess.Should().BeTrue();
        (await otherHarness.Service.ShareAsync(create.Value, new SavedSearchShareInput(
            nameof(SavedSearchSharingScope.Shared), null))).IsSuccess.Should().BeTrue();

        var list = await harness.Service.ListAccessibleAsync(Registry);

        // Caller (Owner) sees the Shared row owned by NonOwner.
        list.Should().ContainSingle(i => i.Name == "other-shared");
    }

    [Fact]
    public async Task ListAccessible_ReturnsGroupRows_WhenCallerInGroup()
    {
        var harness = Harness.Create();
        // Caller is a member of "pensions.examiners".
        await SeedUserProfileAsync(harness.Db, Harness.OwnerUserId, ["pensions.examiners"]);
        await SeedUserProfileAsync(harness.Db, Harness.NonOwnerUserId, ["pensions.examiners"]);

        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");
        var create = await otherHarness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "group-row", "{}", IsShared: false));
        create.IsSuccess.Should().BeTrue();
        (await otherHarness.Service.ShareAsync(create.Value, new SavedSearchShareInput(
            nameof(SavedSearchSharingScope.Group), "pensions.examiners"))).IsSuccess.Should().BeTrue();

        var list = await harness.Service.ListAccessibleAsync(Registry);

        list.Should().ContainSingle(i => i.Name == "group-row");
    }

    [Fact]
    public async Task ListAccessible_ExcludesGroupRows_WhenCallerNotInGroup()
    {
        var harness = Harness.Create();
        // Caller is NOT in pensions.examiners.
        await SeedUserProfileAsync(harness.Db, Harness.OwnerUserId, ["other.group"]);
        await SeedUserProfileAsync(harness.Db, Harness.NonOwnerUserId, ["pensions.examiners"]);

        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");
        var create = await otherHarness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "group-row", "{}", IsShared: false));
        create.IsSuccess.Should().BeTrue();
        (await otherHarness.Service.ShareAsync(create.Value, new SavedSearchShareInput(
            nameof(SavedSearchSharingScope.Group), "pensions.examiners"))).IsSuccess.Should().BeTrue();

        var list = await harness.Service.ListAccessibleAsync(Registry);

        list.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAccessible_FiltersByRegistry()
    {
        var harness = Harness.Create();
        await SeedUserProfileAsync(harness.Db, Harness.OwnerUserId, []);
        await SeedUserProfileAsync(harness.Db, Harness.NonOwnerUserId, []);
        var otherHarness = harness.WithCaller(Harness.NonOwnerUserId, "SQID-OTHER");

        // Two Shared rows by NonOwner on different registries — only Contributors should show.
        var c1 = await otherHarness.Service.CreateAsync(new SavedSearchCreateInput(Registry, "match", "{}", false));
        (await otherHarness.Service.ShareAsync(c1.Value, new SavedSearchShareInput(nameof(SavedSearchSharingScope.Shared), null))).IsSuccess.Should().BeTrue();
        var c2 = await otherHarness.Service.CreateAsync(new SavedSearchCreateInput("Insured", "elsewhere", "{}", false));
        (await otherHarness.Service.ShareAsync(c2.Value, new SavedSearchShareInput(nameof(SavedSearchSharingScope.Shared), null))).IsSuccess.Should().BeTrue();

        var list = await harness.Service.ListAccessibleAsync(Registry);

        list.Should().ContainSingle(i => i.Name == "match");
    }

    /// <summary>
    /// Inserts a UserProfile row used by the group-membership lookup performed by
    /// <c>ListAccessibleAsync</c>. The harness covers MAX two distinct users so this
    /// helper is intentionally minimal: caller-id (long), groups (list of group codes).
    /// </summary>
    /// <param name="db">EF Core context.</param>
    /// <param name="userId">Internal user id (matches the caller's UserId).</param>
    /// <param name="groups">Group codes to assign to the user.</param>
    private static async Task SeedUserProfileAsync(CnasDbContext db, long userId, IEnumerable<string> groups)
    {
        // The InMemory provider lets us set the PK explicitly via the Id property.
        var existing = await db.UserProfiles.SingleOrDefaultAsync(u => u.Id == userId);
        if (existing is not null)
        {
            existing.Groups = groups.ToList();
            await db.SaveChangesAsync();
            return;
        }

        db.UserProfiles.Add(new UserProfile
        {
            Id = userId,
            DisplayName = $"User {userId}",
            Groups = groups.ToList(),
            IsActive = true,
            CreatedAtUtc = ClockNow,
        });
        await db.SaveChangesAsync();
    }

    // ─────────────────────── Counter ───────────────────────

    [Fact]
    public async Task Counter_cnas_saved_search_saved_IncrementsOnCreateAndUpdate()
    {
        using var capture = new MetricCapture("cnas.saved_search.saved");
        var harness = Harness.Create();

        var create = await harness.Service.CreateAsync(new SavedSearchCreateInput(
            Registry, "n1", "{}", IsShared: false));
        create.IsSuccess.Should().BeTrue();

        var update = await harness.Service.UpdateAsync(create.Value, new SavedSearchUpdateInput(
            "n1", "{\"v\":2}", IsShared: true));
        update.IsSuccess.Should().BeTrue();

        // One increment for create + one for update = 2. Idempotent-create on the same
        // triple does NOT increment because no new write occurred — covered by the
        // duplicate test above.
        capture.TotalIncrement.Should().Be(2);
    }

    // ─────────────────────── Harness ───────────────────────

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-savedsearch-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>
    /// MeterListener capture for a single instrument name on the CNAS meter. Mirrors the
    /// helper used by the other meter-aware tests in this assembly.
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

    private sealed class Harness
    {
        /// <summary>UserId of the owner (default caller).</summary>
        public const long OwnerUserId = 1001L;

        /// <summary>UserId of a non-owner used by sharing tests.</summary>
        public const long NonOwnerUserId = 1002L;

        public required CnasDbContext Db { get; init; }
        public required SavedSearchService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create(SavedSearchOptions? opts = null)
        {
            var db = CreateContext();
            return BuildAround(db, OwnerUserId, "SQID-OWNER", new StubClock(ClockNow), opts);
        }

        /// <summary>Builds a sibling harness sharing the same DB but a different caller identity.</summary>
        public Harness WithCaller(long userId, string userSqid) =>
            BuildAround(Db, userId, userSqid, new StubClock(ClockNow), null, Sqids, Audit);

        private static Harness BuildAround(
            CnasDbContext db,
            long callerUserId,
            string callerSqid,
            ICnasTimeProvider clock,
            SavedSearchOptions? opts,
            ISqidService? sharedSqids = null,
            IAuditService? sharedAudit = null)
        {
            var sqids = sharedSqids ?? Substitute.For<ISqidService>();
            if (sharedSqids is null)
            {
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
            }

            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(callerUserId);
            caller.UserSqid.Returns(callerSqid);
            caller.Roles.Returns(["cnas-user"]);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns($"corr-{callerUserId}");

            var audit = sharedAudit ?? Substitute.For<IAuditService>();
            if (sharedAudit is null)
            {
                audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(Result.Success()));
            }

            var resolvedOpts = Options.Create(opts ?? new SavedSearchOptions());

            var service = new SavedSearchService(db, caller, sqids, clock, audit, resolvedOpts);
            return new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
                Audit = audit,
            };
        }
    }
}
