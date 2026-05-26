using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="ContributorService"/>. Uses EF Core InMemory for
/// the persistence backend and NSubstitute for the surrounding collaborators (sqids,
/// audit, caller, clock). The search path's ILIKE-vs-Contains seam is covered by
/// <see cref="SearchAsync_WithFilter_FiltersByDenumireOrIdno"/>.
/// </summary>
public class ContributorServiceTests
{
    /// <summary>Deterministic clock used across the suite to make audit/snapshot fields stable.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Valid IDNO satisfying [1-9][0-9]{12} + mod-10 checksum (see validator tests).</summary>
    private const string ValidIdnoA = "1003600012346";

    /// <summary>Second valid IDNO (different last digit family) — used for duplicate/search cases.</summary>
    /// <remarks>
    /// "200000000000" weighted: 14+0+0+0+0+0+0+0+0+0+0+0 = 14; (10-14%10)%10 = 6.
    /// So "2000000000006" is valid.
    /// </remarks>
    private const string ValidIdnoB = "2000000000006";

    /// <summary>Roles assigned to the simulated caller for audit attribution.</summary>
    private static readonly string[] CallerRoles = ["cnas-user"];

    // ─────────────────────── RegisterAsync ───────────────────────

    [Fact]
    public async Task RegisterAsync_InvalidIdno_ReturnsInvalidIdno()
    {
        var harness = Harness.Create();
        var input = new ContributorRegistrationInput("not-an-idno", "SRL X", null, null);

        var result = await harness.Service.RegisterAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    [Fact]
    public async Task RegisterAsync_DuplicateIdno_ReturnsConflict()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: ValidIdnoA);
        var input = new ContributorRegistrationInput(ValidIdnoA, "Duplicate", null, null);

        var result = await harness.Service.RegisterAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task RegisterAsync_HappyPath_PersistsAndReturnsSqid()
    {
        var harness = Harness.Create();
        var input = new ContributorRegistrationInput(ValidIdnoA, "SRL Exemplu", "1170", "47111");

        var result = await harness.Service.RegisterAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().StartWith("SQID-");

        var persisted = await harness.Db.Contributors.SingleAsync(c => c.Idno == ValidIdnoA);
        persisted.Denumire.Should().Be("SRL Exemplu");
        persisted.CfojCode.Should().Be("1170");
        persisted.CaemCode.Should().Be("47111");
        persisted.IsActive.Should().BeTrue();
        persisted.IsInsolvent.Should().BeFalse();
        persisted.RegisteredAtUtc.Should().Be(ClockNow);
        persisted.CreatedAtUtc.Should().Be(ClockNow);

        // iter-149 — audit payload carries the IDNO hash, not the raw plaintext,
        // because SEC 035 / BUG-007 mandates encryption-at-rest of national
        // identifiers; the audit row reuses the hash shadow column so the
        // payload stays searchable without leaking PII.
        await harness.Audit.Received(1).RecordAsync(
            "CONTRIBUTOR.REGISTERED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(Contributor),
            persisted.Id,
            Arg.Is<string>(s =>
                s.Contains("idnoHash", StringComparison.Ordinal) &&
                !s.Contains(ValidIdnoA, StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── GetByIdAsync ───────────────────────

    [Fact]
    public async Task GetByIdAsync_InvalidSqid_ReturnsInvalidSqid()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("garbage").Returns(Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid"));

        var result = await harness.Service.GetByIdAsync("garbage");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("missing").Returns(Result<long>.Success(99999L));

        var result = await harness.Service.GetByIdAsync("missing");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetByIdAsync_HappyPath_ReturnsContributor()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Alpha SRL");
        harness.Sqids.TryDecode("ALPHA").Returns(Result<long>.Success(entity.Id));

        var result = await harness.Service.GetByIdAsync("ALPHA");

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be($"SQID-{entity.Id}");
        result.Value.Idno.Should().Be(ValidIdnoA);
        result.Value.Denumire.Should().Be("Alpha SRL");
    }

    // ─────────────────────── GetByIdnoAsync ───────────────────────

    [Fact]
    public async Task GetByIdnoAsync_InvalidIdno_ReturnsInvalidIdno()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GetByIdnoAsync("not-an-idno");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    [Fact]
    public async Task GetByIdnoAsync_NotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        // Valid IDNO but no row present.
        var result = await harness.Service.GetByIdnoAsync(ValidIdnoA);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── SearchAsync ───────────────────────

    [Fact]
    public async Task SearchAsync_NoFilter_ReturnsAllActive()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Alpha SRL");
        await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Beta SRL");

        var result = await harness.Service.SearchAsync(null, new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.TotalCount.Should().Be(2);
        // Ordered by Denumire ascending.
        result.Value.Items[0].Denumire.Should().Be("Alpha SRL");
        result.Value.Items[1].Denumire.Should().Be("Beta SRL");
    }

    [Fact]
    public async Task SearchAsync_WithFilter_FiltersByDenumireOrIdno()
    {
        // The InMemory provider used here does not translate EF.Functions.ILike, so the
        // service falls back to an OrdinalIgnoreCase Contains on the client side. The
        // behaviour stays equivalent to a SQL ILIKE — only the execution path differs.
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Alpha SRL");
        await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Beta SRL");

        var byDenumire = await harness.Service.SearchAsync("alpha", new PageRequest(1, 10));
        byDenumire.IsSuccess.Should().BeTrue();
        byDenumire.Value.Items.Should().ContainSingle().Which.Denumire.Should().Be("Alpha SRL");

        var byIdno = await harness.Service.SearchAsync(ValidIdnoB, new PageRequest(1, 10));
        byIdno.IsSuccess.Should().BeTrue();
        byIdno.Value.Items.Should().ContainSingle().Which.Idno.Should().Be(ValidIdnoB);
    }

    /// <summary>
    /// R0833 / iter 136 — pins the "Registrul insolvabililor" browser surface.
    /// Each <see cref="ContributorListItem"/> row carries the contributor's
    /// <see cref="Contributor.IsInsolvent"/> flag so a UI consumer can branch on it
    /// (filter the list to insolvent contributors / colour-code them) without
    /// fetching the full <see cref="ContributorOutput"/>. This regression test
    /// fixes the projection so a future refactor cannot accidentally drop the
    /// flag from the registry browser response.
    /// </summary>
    [Fact]
    public async Task SearchAsync_PreservesIsInsolventOnEveryRow()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Solvent SRL", isInsolvent: false);
        await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Insolvent SRL", isInsolvent: true);

        var result = await harness.Service.SearchAsync(null, new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);

        // The list is ordered by Denumire ascending: "Insolvent SRL" precedes "Solvent SRL".
        var insolventRow = result.Value.Items.Single(r => r.Idno == ValidIdnoB);
        insolventRow.IsInsolvent.Should().BeTrue(
            because: "Registrul insolvabililor consumers need the flag on each registry row.");

        var solventRow = result.Value.Items.Single(r => r.Idno == ValidIdnoA);
        solventRow.IsInsolvent.Should().BeFalse(
            because: "non-insolvent contributors must round-trip the IsInsolvent=false flag without flipping.");
    }

    // ─────────────────────── R0162 — diacritic-insensitive search ───────────────────────

    /// <summary>
    /// R0162 / CF 03.13 — an ASCII query (e.g. <c>"Taranu"</c>) must match a
    /// diacritic-bearing <c>Denumire</c> (e.g. <c>"Țăranu SRL"</c>). On the Postgres
    /// path this routes through <c>unaccent(col)</c>; on the InMemory provider the
    /// service folds both sides with <see cref="Application.Search.DiacriticFolding"/>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_AsciiQuery_MatchesDiacriticDenumire()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Țăranu SRL");
        await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Popescu SRL");

        var result = await harness.Service.SearchAsync("Taranu", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.Denumire.Should().Be("Țăranu SRL");
    }

    /// <summary>
    /// R0162 / CF 03.13 — a diacritic-bearing query (e.g. <c>"Țăranu"</c>) must also
    /// match a plain-ASCII <c>Denumire</c> (e.g. <c>"Taranu SRL"</c>). Insensitivity
    /// must be symmetric — the fold normalises both sides to the same canonical form.
    /// </summary>
    [Fact]
    public async Task SearchAsync_DiacriticQuery_MatchesAsciiDenumire()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Taranu SRL");
        await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Popescu SRL");

        var result = await harness.Service.SearchAsync("Țăranu", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.Denumire.Should().Be("Taranu SRL");
    }

    /// <summary>
    /// R0162 / CF 03.13 — both sides carrying diacritics is the happy path: the fold
    /// reduces both to the same canonical form and the substring match succeeds.
    /// Also exercises case-insensitivity (lowercase query vs title-case <c>Denumire</c>).
    /// </summary>
    [Fact]
    public async Task SearchAsync_BothDiacriticAndDifferentCase_Matches()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Țăranu Ștefan SRL");
        await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Popescu SRL");

        var result = await harness.Service.SearchAsync("țăranu ștefan", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.Denumire.Should().Be("Țăranu Ștefan SRL");
    }

    // ─────────────────────── R0164 — wildcard mask filters (UI 012 / CF 03.02) ───────────────────────

    /// <summary>
    /// R0164 / UI 012 / CF 03.02 — a wildcard query <c>*ESCU</c> (Windows file-mask
    /// convention for "ends with ESCU") must match only contributors whose <c>Denumire</c>
    /// terminates with the literal substring. The <c>*</c> at the prefix translates to
    /// <c>%</c> in <c>LIKE</c> (and to <c>.*</c> with right-anchor in the InMemory
    /// regex path). Confirms the explicit-wildcard branch wins — no implicit
    /// <c>%...%</c> wrap that would otherwise also surface <c>"Popescu Ion"</c>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_StarPrefixMask_MatchesEndsWith()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Popescu");
        await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Popovescu");
        await harness.SeedContributorAsync(idno: "1003600099992", denumire: "Ion");

        var result = await harness.Service.SearchAsync("*escu", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Select(i => i.Denumire).Should().BeEquivalentTo(["Popescu", "Popovescu"]);
    }

    // ─────────────────────── MarkInsolvent / MarkSolvent ───────────────────────

    [Fact]
    public async Task MarkInsolventAsync_HappyPath_FlipsFlagAndAudits()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA);
        harness.Sqids.TryDecode("ID").Returns(Result<long>.Success(entity.Id));

        var result = await harness.Service.MarkInsolventAsync("ID");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.Contributors.SingleAsync(c => c.Id == entity.Id);
        reloaded.IsInsolvent.Should().BeTrue();
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "CONTRIBUTOR.INSOLVENT_SET",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Contributor),
            entity.Id,
            Arg.Is<string>(s => s.Contains("\"isInsolvent\":true")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkSolventAsync_HappyPath_FlipsFlagAndAudits()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA, isInsolvent: true);
        harness.Sqids.TryDecode("ID").Returns(Result<long>.Success(entity.Id));

        var result = await harness.Service.MarkSolventAsync("ID");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.Contributors.SingleAsync(c => c.Id == entity.Id);
        reloaded.IsInsolvent.Should().BeFalse();

        await harness.Audit.Received(1).RecordAsync(
            "CONTRIBUTOR.SOLVENT_RESTORED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Contributor),
            entity.Id,
            Arg.Is<string>(s => s.Contains("\"isInsolvent\":false")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── DeactivateAsync ───────────────────────

    [Fact]
    public async Task DeactivateAsync_HappyPath_SoftDeletes()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA);
        harness.Sqids.TryDecode("ID").Returns(Result<long>.Success(entity.Id));

        var result = await harness.Service.DeactivateAsync("ID");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.Contributors.SingleAsync(c => c.Id == entity.Id);
        reloaded.IsActive.Should().BeFalse();
        reloaded.DeregisteredAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "CONTRIBUTOR.DEACTIVATED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Contributor),
            entity.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── IsInsuredAsync ───────────────────────

    [Fact]
    public async Task IsInsuredAsync_ActiveContributor_ReturnsTrue()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: ValidIdnoA);

        var result = await harness.Service.IsInsuredAsync(ValidIdnoA, ClockNow);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsInsured.Should().BeTrue();
        result.Value.AsOfUtc.Should().Be(ClockNow);
        result.Value.Idno.Should().Be(ValidIdnoA);
    }

    [Fact]
    public async Task IsInsuredAsync_DeregisteredBeforeAsOfDate_ReturnsFalse()
    {
        var harness = Harness.Create();
        var deregistered = ClockNow.AddDays(-10);
        // Seed an inactive (de-registered) contributor — already invisible to the active filter.
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA);
        entity.IsActive = false;
        entity.DeregisteredAtUtc = deregistered;
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.IsInsuredAsync(ValidIdnoA, ClockNow);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsInsured.Should().BeFalse();
    }

    [Fact]
    public async Task IsInsuredAsync_UnknownIdno_ReturnsFalse()
    {
        var harness = Harness.Create();
        // No row at all.
        var result = await harness.Service.IsInsuredAsync(ValidIdnoA, ClockNow);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsInsured.Should().BeFalse();
    }

    [Fact]
    public async Task IsInsuredAsync_InvalidIdno_ReturnsInvalidIdno()
    {
        var harness = Harness.Create();

        var result = await harness.Service.IsInsuredAsync("bogus", ClockNow);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-contrib-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning a fixed instant for deterministic tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ContributorService Service { get; init; }
        public required IAuditService Audit { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }
        public required ICnasTimeProvider Clock { get; init; }

        /// <summary>Wires up the SUT with NSubstitute fakes and a fresh InMemory DB.</summary>
        public static Harness Create()
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
            caller.Roles.Returns(CallerRoles);
            caller.SourceIp.Returns("127.0.0.1");
            caller.CorrelationId.Returns("corr-1");

            var service = new ContributorService(db, sqids, clock, caller, audit, IdHashHelper.Instance);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
                Caller = caller,
                Sqids = sqids,
                Clock = clock,
            };
        }

        /// <summary>Inserts an active <see cref="Contributor"/> with sane defaults and returns the entity.</summary>
        public async Task<Contributor> SeedContributorAsync(
            string idno,
            string denumire = "Default SRL",
            bool isInsolvent = false)
        {
            var entity = new Contributor
            {
                Idno = idno,
                IdnoHash = IdHashHelper.Hash(idno),
                Denumire = denumire,
                CfojCode = null,
                CaemCode = null,
                IsInsolvent = isInsolvent,
                RegisteredAtUtc = ClockNow.AddDays(-30),
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.Contributors.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }
    }

    // ════════════════════ R0305 / TOR Annex 1 — BP 1.2 / 1.3 / 1.4 / 1.5 / 1.9 ════════════════════

    /// <summary>
    /// R0305 / BP 1.2 — UpdateAttributesAsync must refuse to mutate a deactivated row.
    /// Mirrors the operator-flow requirement: reactivate (BP 1.4) before editing.
    /// </summary>
    [Fact]
    public async Task UpdateAttributesAsync_WhenDeactivated_ReturnsConflict()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Old SRL");
        entity.IsDeactivated = true;
        entity.DeactivatedAtUtc = ClockNow.AddDays(-1);
        entity.DeactivationReason = "suspended";
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.UpdateAttributesAsync(
            entity.Id,
            new ContributorAttributesUpdateDto("New SRL", null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>
    /// R0305 / BP 1.2 — happy path: Denumire / CFOJ / CAEM are updated and a Notice
    /// CONTRIBUTOR.UPDATED audit row is emitted.
    /// </summary>
    [Fact]
    public async Task UpdateAttributesAsync_HappyPath_PersistsChangesAndAudits()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Old SRL");

        var result = await harness.Service.UpdateAttributesAsync(
            entity.Id,
            new ContributorAttributesUpdateDto("New SRL", "1180", "47222"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Denumire.Should().Be("New SRL");
        result.Value.CfojCode.Should().Be("1180");
        result.Value.CaemCode.Should().Be("47222");

        var reloaded = await harness.Db.Contributors.SingleAsync(c => c.Id == entity.Id);
        reloaded.Denumire.Should().Be("New SRL");
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "CONTRIBUTOR.UPDATED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(Contributor),
            entity.Id,
            Arg.Is<string>(s => s.Contains("New SRL", StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// R0305 / BP 1.3 — DeactivateAsync(long, reason) flips <c>IsDeactivated=true</c>,
    /// stamps the timestamp + reason, and emits a Critical CONTRIBUTOR.DEACTIVATED_BP
    /// audit row.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_HappyPath_FlipsFlagAndAuditsCritical()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA);

        var result = await harness.Service.DeactivateAsync(entity.Id, "Operator request", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.Contributors.SingleAsync(c => c.Id == entity.Id);
        reloaded.IsDeactivated.Should().BeTrue();
        reloaded.DeactivatedAtUtc.Should().Be(ClockNow);
        reloaded.DeactivationReason.Should().Be("Operator request");

        await harness.Audit.Received(1).RecordAsync(
            "CONTRIBUTOR.DEACTIVATED_BP",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Contributor),
            entity.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// R0305 / BP 1.3 — re-deactivating an already-deactivated row must surface a
    /// Conflict (the operator should reactivate first to change the reason).
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_AlreadyDeactivated_ReturnsConflict()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA);
        entity.IsDeactivated = true;
        entity.DeactivatedAtUtc = ClockNow.AddDays(-1);
        entity.DeactivationReason = "earlier";
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.DeactivateAsync(entity.Id, "Another reason", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>
    /// R0305 / BP 1.4 — ReactivateAsync clears the deactivation flag, audit row is
    /// Critical CONTRIBUTOR.REACTIVATED.
    /// </summary>
    [Fact]
    public async Task ReactivateAsync_HappyPath_FlipsFlagAndAuditsCritical()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA);
        entity.IsDeactivated = true;
        entity.DeactivatedAtUtc = ClockNow.AddDays(-1);
        entity.DeactivationReason = "earlier reason";
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.ReactivateAsync(entity.Id, "Resumed business", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.Contributors.SingleAsync(c => c.Id == entity.Id);
        reloaded.IsDeactivated.Should().BeFalse();
        reloaded.DeactivatedAtUtc.Should().BeNull();
        reloaded.DeactivationReason.Should().BeNull();

        await harness.Audit.Received(1).RecordAsync(
            "CONTRIBUTOR.REACTIVATED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Contributor),
            entity.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// R0305 / BP 1.4 — reactivation refused on terminal-state rows (deceased).
    /// </summary>
    [Fact]
    public async Task ReactivateAsync_WhenDeceased_ReturnsConflict()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA);
        entity.IsDeactivated = true;
        entity.IsDeceased = true;
        entity.DeceasedAtUtc = ClockNow.AddDays(-30);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.ReactivateAsync(entity.Id, "Try", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>
    /// R0305 / BP 1.5 — MergeDuplicatesAsync points the duplicate at the survivor,
    /// flips <c>IsDeactivated=true</c>, sets the canonical reason, and emits Critical
    /// CONTRIBUTOR.MERGED audit.
    /// </summary>
    [Fact]
    public async Task MergeDuplicatesAsync_HappyPath_SetsMergePointerAndAudits()
    {
        var harness = Harness.Create();
        var duplicate = await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Dup SRL");
        var survivor = await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Surv SRL");

        var result = await harness.Service.MergeDuplicatesAsync(duplicate.Id, survivor.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.Contributors.SingleAsync(c => c.Id == duplicate.Id);
        reloaded.MergedIntoContributorId.Should().Be(survivor.Id);
        reloaded.IsDeactivated.Should().BeTrue();
        reloaded.DeactivationReason.Should().Contain("merged into");

        await harness.Audit.Received(1).RecordAsync(
            "CONTRIBUTOR.MERGED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Contributor),
            duplicate.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// R0305 / BP 1.5 — refuses to merge when the survivor is itself already merged
    /// (would create a chain that violates the "MergedIntoContributorId points at a
    /// non-merged row" invariant).
    /// </summary>
    [Fact]
    public async Task MergeDuplicatesAsync_WhenSurvivorAlreadyMerged_ReturnsForbidden()
    {
        var harness = Harness.Create();
        var duplicate = await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Dup SRL");
        var survivor = await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Surv SRL");
        var chained = await harness.SeedContributorAsync(idno: "1003600099992", denumire: "Chain SRL");
        survivor.MergedIntoContributorId = chained.Id;
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.MergeDuplicatesAsync(duplicate.Id, survivor.Id, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    /// <summary>
    /// R0305 / BP 1.5 — same-id merge rejected with ValidationFailed.
    /// </summary>
    [Fact]
    public async Task MergeDuplicatesAsync_SameId_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA);

        var result = await harness.Service.MergeDuplicatesAsync(entity.Id, entity.Id, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// R0305 / BP 1.9 — IDNP-leading-with-2 (natural person) sets IsDeceased=true.
    /// IDNO leading-with-1 (legal person) sets IsDissolved=true. Both audit Critical.
    /// </summary>
    [Fact]
    public async Task MarkDeceasedOrDissolvedAsync_NaturalAndLegal_SetCorrectFlag()
    {
        var harness = Harness.Create();
        var natural = await harness.SeedContributorAsync(idno: ValidIdnoB, denumire: "Mr Natural"); // starts with '2'
        var legal = await harness.SeedContributorAsync(idno: ValidIdnoA, denumire: "Legal SRL");   // starts with '1'

        var date = new DateOnly(2026, 4, 15);
        var natResult = await harness.Service.MarkDeceasedOrDissolvedAsync(natural.Id, date, CancellationToken.None);
        var legResult = await harness.Service.MarkDeceasedOrDissolvedAsync(legal.Id, date, CancellationToken.None);

        natResult.IsSuccess.Should().BeTrue();
        legResult.IsSuccess.Should().BeTrue();

        var reloadedNat = await harness.Db.Contributors.SingleAsync(c => c.Id == natural.Id);
        reloadedNat.IsDeceased.Should().BeTrue();
        reloadedNat.IsDissolved.Should().BeFalse();
        reloadedNat.IsDeactivated.Should().BeTrue();

        var reloadedLeg = await harness.Db.Contributors.SingleAsync(c => c.Id == legal.Id);
        reloadedLeg.IsDissolved.Should().BeTrue();
        reloadedLeg.IsDeceased.Should().BeFalse();
        reloadedLeg.IsDeactivated.Should().BeTrue();

        await harness.Audit.Received(2).RecordAsync(
            "CONTRIBUTOR.DECEASED_OR_DISSOLVED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Contributor),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// R0305 / BP 1.3 — observe the <c>cnas.contributor.bp_invoked{bp=Deactivate}</c>
    /// counter increments per invocation. The counter is created on
    /// <see cref="Cnas.Ps.Infrastructure.Observability.CnasMeter"/> and surfaces via the
    /// .NET diagnostics <see cref="System.Diagnostics.Metrics.MeterListener"/>.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_IncrementsBpInvokedCounter()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedContributorAsync(idno: ValidIdnoA);

        long observed = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "cnas.contributor.bp_invoked")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "bp" && tag.Value is string s && s == "Deactivate")
                {
                    Interlocked.Add(ref observed, measurement);
                }
            }
        });
        listener.Start();

        var result = await harness.Service.DeactivateAsync(entity.Id, "Counter test", CancellationToken.None);
        listener.RecordObservableInstruments();

        result.IsSuccess.Should().BeTrue();
        observed.Should().Be(1);
    }
}
