using System;
using System.Globalization;
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
/// Pins the post-encryption search semantics of <see cref="InsuredPersonService.SearchAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Sibling to <see cref="ContributorSearchOnEncryptedColumnTests"/>. The bug and fix are
/// identical in shape — the encrypted column here is <see cref="InsuredPerson.Idnp"/> rather
/// than <see cref="Contributor.Idno"/>, and the matching hash shadow is
/// <see cref="InsuredPerson.IdnpHash"/>.
/// </para>
/// <para>
/// As with the contributor harness, the SUT is built on the encryptor-enabled
/// <see cref="CnasDbContext(DbContextOptions{CnasDbContext}, IFieldEncryptor)"/> ctor so
/// the converter is actually exercised; this defeats the silent-pass mode in which the
/// InMemory store would hold plaintext.
/// </para>
/// </remarks>
public class InsuredPersonSearchOnEncryptedColumnTests
{
    /// <summary>Deterministic clock so audit/snapshot fields stay stable across runs.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

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
    /// Builds a valid 13-digit IDNP from a 12-digit prefix using the mod-10 weighted checksum.
    /// Mirrors the validator and the helper in <c>InsuredPersonServiceTests</c> so this test
    /// file is self-contained.
    /// </summary>
    /// <param name="twelveDigitPrefix">First 12 digits of the IDNP (century prefix included).</param>
    /// <returns>13-digit canonical IDNP accepted by <c>Idnp.TryCreate</c>.</returns>
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

    /// <summary>First valid 13-digit IDNP — used for hash-equality search.</summary>
    private static readonly string FullIdnpA = BuildIdnp("200012345678");

    /// <summary>Second valid 13-digit IDNP — used to prove the hash lookup is selective.</summary>
    private static readonly string FullIdnpB = BuildIdnp("199912345678");

    /// <summary>
    /// A full 13-digit IDNP query must resolve through the IDNP-hash shadow column —
    /// not through ILIKE/Contains on the encrypted plaintext column — and return only
    /// the matching insured person.
    /// </summary>
    [Fact]
    public async Task Search_FullIdnp_FindsRowByHashLookup()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: FullIdnpA, lastName: "Alpha", firstName: "Ana");
        await harness.SeedInsuredAsync(idnp: FullIdnpB, lastName: "Beta", firstName: "Boris");

        var result = await harness.Service.SearchAsync(FullIdnpA, new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(
            "the hash-equality branch must isolate exactly the matching insured person.");
        result.Value.Items[0].Idnp.Should().Be(FullIdnpA);
    }

    /// <summary>
    /// A partial-IDNP query (digit prefix) must NOT match by IDNP — but the same query
    /// against a last-name substring must still resolve.
    /// </summary>
    [Fact]
    public async Task Search_PartialIdnp_DoesNotMatchByIdnpButStillMatchesByLastName()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: FullIdnpA, lastName: "Popescu", firstName: "Ion");

        // 1) Partial digits — must NOT find the row by IDNP.
        var partial = await harness.Service.SearchAsync("20001", new PageRequest(1, 10));
        partial.IsSuccess.Should().BeTrue();
        partial.Value.Items.Should().BeEmpty(
            "partial-IDNP search is intentionally unsupported once the column is encrypted.");

        // 2) Last-name substring — must still find the row.
        var byName = await harness.Service.SearchAsync("Popescu", new PageRequest(1, 10));
        byName.IsSuccess.Should().BeTrue();
        byName.Value.Items.Should().ContainSingle()
            .Which.Idnp.Should().Be(FullIdnpA);
    }

    /// <summary>
    /// The hash-equality branch must canonicalize (Trim + ToUpperInvariant) the query
    /// before hashing, mirroring <see cref="IDeterministicHasher.ComputeHash"/>'s contract.
    /// </summary>
    [Fact]
    public async Task Search_FullIdnp_CanonicalizesInputBeforeHashing()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: FullIdnpA, lastName: "Popescu", firstName: "Ion");

        var result = await harness.Service.SearchAsync($"  {FullIdnpA}  ", new PageRequest(1, 10));

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle()
            .Which.Idnp.Should().Be(FullIdnpA);
    }

    /// <summary>
    /// Regression check for the no-filter path: an empty/whitespace query must still
    /// return all active insured persons (the encrypted-column fix must not change this).
    /// </summary>
    [Fact]
    public async Task Search_EmptyQuery_ReturnsAllActive()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredAsync(idnp: FullIdnpA, lastName: "Alpha", firstName: "Ana");
        await harness.SeedInsuredAsync(idnp: FullIdnpB, lastName: "Beta", firstName: "Boris");

        var noFilter = await harness.Service.SearchAsync(null, new PageRequest(1, 10));
        noFilter.IsSuccess.Should().BeTrue();
        noFilter.Value.Items.Should().HaveCount(2);
        noFilter.Value.TotalCount.Should().Be(2);

        var whitespaceOnly = await harness.Service.SearchAsync("   ", new PageRequest(1, 10));
        whitespaceOnly.IsSuccess.Should().BeTrue();
        whitespaceOnly.Value.Items.Should().HaveCount(2,
            "whitespace-only queries are equivalent to no filter — IsNullOrWhiteSpace guards the branch.");
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
        public required InsuredPersonService Service { get; init; }

        /// <summary>Creates a fresh harness with a unique in-memory database and a real encryptor.</summary>
        public static Harness Create()
        {
            var encOpts = new FieldEncryptionOptions { Key = Convert.ToBase64String(TestEncryptionKey) };
            IFieldEncryptor encryptor = new AesFieldEncryptor(Options.Create(encOpts));

            var dbOpts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-insured-search-enc-{Guid.NewGuid():N}")
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

            var service = new InsuredPersonService(db, sqids, clock, caller, audit, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds an active insured person with the supplied IDNP/name and matching hash.</summary>
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
