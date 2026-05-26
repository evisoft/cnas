using System.Globalization;
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
/// Integration tests for <see cref="InsuredPersonService"/>. Uses EF Core InMemory for
/// the persistence backend and NSubstitute for the surrounding collaborators (sqids,
/// audit, caller, clock). Mirrors the harness in
/// <see cref="ContributorServiceTests"/> so the two registries behave consistently.
/// </summary>
public class InsuredPersonServiceTests
{
    /// <summary>Deterministic clock used across the suite to make audit/snapshot fields stable.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Builds a valid 13-digit IDNP from a 12-digit prefix, computing the mod-10
    /// weighted checksum the same way <see cref="Cnas.Ps.Core.ValueObjects.Idnp"/> does.
    /// Same helper as in <c>IdnpTests</c> — duplicated here so this test file has zero
    /// dependencies on the Core test project.
    /// </summary>
    /// <param name="twelveDigitPrefix">First 12 digits of the IDNP, century prefix included.</param>
    /// <returns>13-digit canonical IDNP that <c>Idnp.TryCreate</c> will accept.</returns>
    private static string BuildIdnp(string twelveDigitPrefix)
    {
        if (twelveDigitPrefix.Length != 12)
        {
            throw new ArgumentException("Prefix must be 12 digits.", nameof(twelveDigitPrefix));
        }

        int[] weights = { 7, 3, 1 };
        int sum = 0;
        for (int i = 0; i < 12; i++)
        {
            sum += (twelveDigitPrefix[i] - '0') * weights[i % 3];
        }
        int check = (10 - (sum % 10)) % 10;
        return twelveDigitPrefix + check.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>First canonical valid IDNP — used by single-record happy-path tests.</summary>
    private static readonly string ValidIdnpA = BuildIdnp("200012345678");

    /// <summary>Second canonical valid IDNP — used by duplicate/search cases.</summary>
    private static readonly string ValidIdnpB = BuildIdnp("199912345678");

    /// <summary>Roles assigned to the simulated caller for audit attribution.</summary>
    private static readonly string[] CallerRoles = ["cnas-user"];

    // ─────────────────────── RegisterAsync ───────────────────────

    [Fact]
    public async Task RegisterAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var harness = Harness.Create();
        var input = new InsuredPersonRegistrationInput(
            "not-an-idnp", "Popescu", "Ion", null, new DateOnly(1980, 1, 1));

        var result = await harness.Service.RegisterAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    [Fact]
    public async Task RegisterAsync_DuplicateIdnp_ReturnsConflict()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: ValidIdnpA);
        var input = new InsuredPersonRegistrationInput(
            ValidIdnpA, "Duplicate", "Duplicate", null, new DateOnly(1980, 1, 1));

        var result = await harness.Service.RegisterAsync(input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task RegisterAsync_HappyPath_PersistsAndReturnsSqid()
    {
        var harness = Harness.Create();
        var input = new InsuredPersonRegistrationInput(
            ValidIdnpA, "Popescu", "Ion", "Vasilevici", new DateOnly(1980, 5, 12));

        var result = await harness.Service.RegisterAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().StartWith("SQID-");

        var persisted = await harness.Db.InsuredPersons.SingleAsync(p => p.Idnp == ValidIdnpA);
        persisted.LastName.Should().Be("Popescu");
        persisted.FirstName.Should().Be("Ion");
        persisted.Patronymic.Should().Be("Vasilevici");
        persisted.BirthDate.Should().Be(new DateOnly(1980, 5, 12));
        persisted.IsActive.Should().BeTrue();
        persisted.IsDeceased.Should().BeFalse();
        persisted.RegisteredAtUtc.Should().Be(ClockNow);
        persisted.CreatedAtUtc.Should().Be(ClockNow);

        // iter-149 — audit payload carries the IDNP hash, not the raw plaintext,
        // per SEC 035 / BUG-007 encryption-at-rest of national identifiers.
        await harness.Audit.Received(1).RecordAsync(
            "INSURED_PERSON.REGISTERED",
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(InsuredPerson),
            persisted.Id,
            Arg.Is<string>(s =>
                s.Contains("idnpHash", StringComparison.Ordinal) &&
                !s.Contains(ValidIdnpA, StringComparison.Ordinal)),
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
    public async Task GetByIdAsync_HappyPath_ReturnsInsuredPerson()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedInsuredAsync(idnp: ValidIdnpA, lastName: "Alpha", firstName: "Ana");
        harness.Sqids.TryDecode("ALPHA").Returns(Result<long>.Success(entity.Id));

        var result = await harness.Service.GetByIdAsync("ALPHA");

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be($"SQID-{entity.Id}");
        result.Value.Idnp.Should().Be(ValidIdnpA);
        result.Value.LastName.Should().Be("Alpha");
        result.Value.FirstName.Should().Be("Ana");
    }

    // ─────────────────────── GetByIdnpAsync ───────────────────────

    [Fact]
    public async Task GetByIdnpAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GetByIdnpAsync("not-an-idnp");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    [Fact]
    public async Task GetByIdnpAsync_NotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        // Valid IDNP but no row present.
        var result = await harness.Service.GetByIdnpAsync(ValidIdnpA);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── SearchAsync ───────────────────────

    [Fact]
    public async Task SearchAsync_NoFilter_ReturnsAllActive()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: ValidIdnpA, lastName: "Alpha", firstName: "Ana");
        await harness.SeedInsuredAsync(idnp: ValidIdnpB, lastName: "Beta", firstName: "Boris");

        var result = await harness.Service.SearchAsync(null, new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.TotalCount.Should().Be(2);
        // Ordered by LastName ascending.
        result.Value.Items[0].FullName.Should().StartWith("Alpha");
        result.Value.Items[1].FullName.Should().StartWith("Beta");
    }

    [Fact]
    public async Task SearchAsync_WithFilter_FiltersByNameOrIdnp()
    {
        // The InMemory provider used here does not translate EF.Functions.ILike, so the
        // service falls back to an OrdinalIgnoreCase Contains on the client side. The
        // behaviour stays equivalent to a SQL ILIKE — only the execution path differs.
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: ValidIdnpA, lastName: "Alpha", firstName: "Ana");
        await harness.SeedInsuredAsync(idnp: ValidIdnpB, lastName: "Beta", firstName: "Boris");

        var byName = await harness.Service.SearchAsync("alpha", new PageRequest(1, 10));
        byName.IsSuccess.Should().BeTrue();
        byName.Value.Items.Should().ContainSingle().Which.Idnp.Should().Be(ValidIdnpA);

        var byIdnp = await harness.Service.SearchAsync(ValidIdnpB, new PageRequest(1, 10));
        byIdnp.IsSuccess.Should().BeTrue();
        byIdnp.Value.Items.Should().ContainSingle().Which.Idnp.Should().Be(ValidIdnpB);
    }

    // ─────────────────────── R0162 — diacritic-insensitive search ───────────────────────

    /// <summary>
    /// R0162 / CF 03.13 — an ASCII query (e.g. <c>"Stefan"</c>) must match a
    /// diacritic-bearing <c>LastName</c> (e.g. <c>"Ștefan"</c>). On the Postgres
    /// path this routes through <c>unaccent(col)</c>; on the InMemory provider the
    /// service folds both sides with <see cref="Application.Search.DiacriticFolding"/>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_AsciiQuery_MatchesDiacriticLastName()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: ValidIdnpA, lastName: "Ștefan", firstName: "Andrei");
        await harness.SeedInsuredAsync(idnp: ValidIdnpB, lastName: "Popescu", firstName: "Ion");

        var result = await harness.Service.SearchAsync("Stefan", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.FullName.Should().StartWith("Ștefan");
    }

    /// <summary>
    /// R0162 / CF 03.13 — a diacritic query (e.g. <c>"Țăranu"</c>) must match a
    /// plain-ASCII <c>LastName</c> (e.g. <c>"Taranu"</c>). Insensitivity must be
    /// symmetric: the fold normalises both sides to the same canonical form.
    /// </summary>
    [Fact]
    public async Task SearchAsync_DiacriticQuery_MatchesAsciiLastName()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: ValidIdnpA, lastName: "Taranu", firstName: "Andrei");
        await harness.SeedInsuredAsync(idnp: ValidIdnpB, lastName: "Popescu", firstName: "Ion");

        var result = await harness.Service.SearchAsync("Țăranu", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.FullName.Should().StartWith("Taranu");
    }

    /// <summary>
    /// R0162 / CF 03.13 — both sides carrying diacritics: fold reduces them to the
    /// same canonical form and substring match succeeds. Also covers case-insensitivity
    /// on the diacritic letters.
    /// </summary>
    [Fact]
    public async Task SearchAsync_BothDiacriticAndDifferentCase_Matches()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: ValidIdnpA, lastName: "Țăranu", firstName: "Ștefan");
        await harness.SeedInsuredAsync(idnp: ValidIdnpB, lastName: "Popescu", firstName: "Ion");

        var result = await harness.Service.SearchAsync("țăranu", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.FullName.Should().StartWith("Țăranu");
    }

    // ─────────────────────── R0164 — wildcard mask filters (UI 012 / CF 03.02) ───────────────────────

    /// <summary>
    /// R0164 / UI 012 / CF 03.02 — a wildcard query <c>*escu</c> (Windows file-mask
    /// convention for "ends with escu") must match only insured persons whose
    /// <c>LastName</c> ends with the literal substring. The explicit-wildcard branch
    /// must defeat the implicit <c>%...%</c> wrap that would otherwise surface
    /// <c>"Popescu Ion"</c> in a longer composite name.
    /// </summary>
    [Fact]
    public async Task SearchAsync_StarPrefixMask_MatchesEndsWith()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: ValidIdnpA, lastName: "Popescu", firstName: "Ion");
        await harness.SeedInsuredAsync(idnp: ValidIdnpB, lastName: "Popovescu", firstName: "Ana");
        await harness.SeedInsuredAsync(idnp: "1003600099992", lastName: "Ionel", firstName: "Marin");

        var result = await harness.Service.SearchAsync("*escu", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(2);
        result.Value.Items.Select(i => i.FullName)
            .Should().BeEquivalentTo(["Popescu Ion", "Popovescu Ana"]);
    }

    // ─────────────────────── MarkDeceasedAsync ───────────────────────

    [Fact]
    public async Task MarkDeceasedAsync_HappyPath_SetsFlagAndAudits()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedInsuredAsync(idnp: ValidIdnpA);
        harness.Sqids.TryDecode("ID").Returns(Result<long>.Success(entity.Id));
        var dateOfDeath = new DateOnly(2026, 5, 10);

        var result = await harness.Service.MarkDeceasedAsync("ID", dateOfDeath);

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.InsuredPersons.SingleAsync(p => p.Id == entity.Id);
        reloaded.IsDeceased.Should().BeTrue();
        reloaded.DateOfDeath.Should().Be(dateOfDeath);
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "INSURED_PERSON.DECEASED_RECORDED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(InsuredPerson),
            entity.Id,
            Arg.Is<string>(s => s.Contains("2026-05-10")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkDeceasedAsync_NotFound_ReturnsNotFound()
    {
        var harness = Harness.Create();
        harness.Sqids.TryDecode("nope").Returns(Result<long>.Success(99999L));

        var result = await harness.Service.MarkDeceasedAsync("nope", new DateOnly(2026, 1, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ─────────────────────── DeactivateAsync ───────────────────────

    [Fact]
    public async Task DeactivateAsync_HappyPath_SoftDeletes()
    {
        var harness = Harness.Create();
        var entity = await harness.SeedInsuredAsync(idnp: ValidIdnpA);
        harness.Sqids.TryDecode("ID").Returns(Result<long>.Success(entity.Id));

        var result = await harness.Service.DeactivateAsync("ID");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.InsuredPersons.SingleAsync(p => p.Id == entity.Id);
        reloaded.IsActive.Should().BeFalse();
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);

        await harness.Audit.Received(1).RecordAsync(
            "INSURED_PERSON.DEACTIVATED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(InsuredPerson),
            entity.Id,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-insured-{Guid.NewGuid():N}")
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
        public required InsuredPersonService Service { get; init; }
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

            var service = new InsuredPersonService(db, sqids, clock, caller, audit, IdHashHelper.Instance);
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

        /// <summary>Inserts an active <see cref="InsuredPerson"/> with sane defaults and returns the entity.</summary>
        public async Task<InsuredPerson> SeedInsuredAsync(
            string idnp,
            string lastName = "Default",
            string firstName = "Person",
            string? patronymic = null)
        {
            var entity = new InsuredPerson
            {
                Idnp = idnp,
                IdnpHash = IdHashHelper.Hash(idnp),
                LastName = lastName,
                FirstName = firstName,
                Patronymic = patronymic,
                BirthDate = new DateOnly(1980, 1, 1),
                IsDeceased = false,
                RegisteredAtUtc = ClockNow.AddDays(-30),
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.InsuredPersons.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }
    }
}
