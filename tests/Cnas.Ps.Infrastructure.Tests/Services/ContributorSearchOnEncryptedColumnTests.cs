using System;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Security;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Pins the post-encryption search semantics of <see cref="ContributorService.SearchAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Before this batch, <c>SearchAsync</c> applied <c>ILIKE '%idno%'</c> against the
/// <see cref="Contributor.Idno"/> column. After the TOR SEC 035 encryption rollout the
/// column stores AES-256-GCM envelopes (<c>v1:&lt;base64&gt;</c>) rather than the raw 13-digit
/// IDNO, so the ILIKE returned zero rows in production while the InMemory test suite
/// continued to pass (InMemory holds plaintext — encryption converter never sees it).
/// </para>
/// <para>
/// The fix reshapes the filter: when the caller supplies a full 13-digit identifier we
/// hash it (canonicalized) and equality-match against <see cref="Contributor.IdnoHash"/>;
/// otherwise we fall back to name-field substring search only. Partial-IDNO search is
/// intentionally unsupported because it would require a blind-index / n-gram-hash scheme
/// that is out of scope for this batch.
/// </para>
/// <para>
/// The tests below build the SUT on top of the <b>encryptor-enabled</b>
/// <see cref="CnasDbContext(DbContextOptions{CnasDbContext}, IFieldEncryptor)"/> ctor so
/// the value converter is actually wired — that means the seeded <see cref="Contributor.Idno"/>
/// value round-trips through <see cref="AesFieldEncryptor"/> and the regression that
/// silently masked the bug (plaintext InMemory) cannot recur here.
/// </para>
/// </remarks>
public class ContributorSearchOnEncryptedColumnTests
{
    /// <summary>Deterministic clock so audit/snapshot fields stay stable across runs.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>First valid 13-digit IDNO with mod-10 checksum — used for hash-equality search.</summary>
    private const string FullIdnoA = "2000000000006";

    /// <summary>Second valid 13-digit IDNO (different family) — used to prove the hash lookup is selective.</summary>
    private const string FullIdnoB = "1003600012346";

    /// <summary>Roles assigned to the simulated caller for audit attribution.</summary>
    private static readonly string[] CallerRoles = ["cnas-user"];

    /// <summary>32-byte test AES master key — kept distinct from the production key.</summary>
    private static readonly byte[] TestEncryptionKey =
    [
        0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7,
        0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
        0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7,
        0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
    ];

    /// <summary>
    /// A full 13-digit IDNO query must resolve through the IDNO-hash shadow column —
    /// not through ILIKE/Contains on the encrypted plaintext column — and return only
    /// the matching row.
    /// </summary>
    [Fact]
    public async Task Search_FullIdno_FindsRowByHashLookup()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: FullIdnoA, denumire: "Alpha SRL");
        await harness.SeedContributorAsync(idno: FullIdnoB, denumire: "Beta SRL");

        var result = await harness.Service.SearchAsync(FullIdnoA, new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(
            "the hash-equality branch must isolate exactly the matching contributor.");
        result.Value.Items[0].Idno.Should().Be(FullIdnoA);
        result.Value.Items[0].Denumire.Should().Be("Alpha SRL");
    }

    /// <summary>
    /// A partial-IDNO query (a digit prefix) must NOT match by IDNO — substring search on
    /// an encrypted column is impossible — but name-field substring search must still resolve.
    /// </summary>
    [Fact]
    public async Task Search_PartialIdno_DoesNotMatchByIdnoButStillMatchesByName()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: FullIdnoA, denumire: "Acme SRL");

        // 1) Partial digits — must NOT find the contributor by IDNO.
        var partial = await harness.Service.SearchAsync("20000", new PageRequest(1, 10));
        partial.IsSuccess.Should().BeTrue();
        partial.Value.Items.Should().BeEmpty(
            "partial-IDNO search is intentionally unsupported once the column is encrypted.");

        // 2) Name fragment — must still find the contributor.
        var byName = await harness.Service.SearchAsync("Acme", new PageRequest(1, 10));
        byName.IsSuccess.Should().BeTrue();
        byName.Value.Items.Should().ContainSingle()
            .Which.Denumire.Should().Be("Acme SRL");
    }

    /// <summary>
    /// The hash-equality branch must canonicalize (Trim + ToUpperInvariant) the query
    /// before hashing, mirroring <see cref="IDeterministicHasher.ComputeHash"/>'s contract.
    /// </summary>
    [Fact]
    public async Task Search_FullIdno_CanonicalizesInputBeforeHashing()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: FullIdnoA, denumire: "Alpha SRL");

        // Surrounding whitespace must be tolerated — the hasher canonicalizes Trim+ToUpper.
        var result = await harness.Service.SearchAsync($"  {FullIdnoA}  ", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle()
            .Which.Idno.Should().Be(FullIdnoA);
    }

    /// <summary>
    /// A 13-character query that mixes digits with non-digit characters is NOT a national
    /// identifier — the hash branch must be skipped and the query treated as a name substring.
    /// (Critically: we do NOT extract the digit prefix and hash that — extraction would still
    /// be a partial match, which is exactly what we cannot support post-encryption.)
    /// </summary>
    [Fact]
    public async Task Search_FullIdnoMixedWithNonDigitNoise_FallsBackToNameSearchOnly()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync(idno: FullIdnoA, denumire: "Alpha SRL");

        // "20000abc" — 8 chars, mixed digits/letters — must NOT trigger the hash branch.
        // No name contains "20000abc" either, so we expect zero matches.
        var result = await harness.Service.SearchAsync("20000abc", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty(
            "mixed digit/letter input must NOT be hash-equality-matched; it is treated as a name fragment.");
    }

    // ─────────────────────── Harness ───────────────────────

    /// <summary>Stub clock returning a fixed instant for deterministic tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT and its collaborators on top of an encryptor-aware DbContext.</summary>
    private sealed class Harness
    {
        /// <summary>Encryptor-aware DbContext — the value converter is wired.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>System under test — production-shape DI graph.</summary>
        public required ContributorService Service { get; init; }

        /// <summary>Creates a fresh harness with a unique in-memory database and a real encryptor.</summary>
        public static Harness Create()
        {
            var encOpts = new FieldEncryptionOptions { Key = Convert.ToBase64String(TestEncryptionKey) };
            IFieldEncryptor encryptor = new AesFieldEncryptor(Options.Create(encOpts));

            var dbOpts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-contrib-search-enc-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(dbOpts, encryptor);

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
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds an active contributor with the supplied IDNO/Denumire and matching hash.</summary>
        public async Task<Contributor> SeedContributorAsync(string idno, string denumire = "Default SRL")
        {
            var entity = new Contributor
            {
                Idno = idno,
                IdnoHash = IdHashHelper.Hash(idno),
                Denumire = denumire,
                CfojCode = null,
                CaemCode = null,
                IsInsolvent = false,
                RegisteredAtUtc = ClockNow.AddDays(-30),
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.Contributors.Add(entity);
            await Db.SaveChangesAsync();
            return entity;
        }
    }
}
