using System;
using System.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Persistence.Conversion;
using Cnas.Ps.Infrastructure.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for the national-identifier encryption + hash-shadow-column wiring
/// (TOR SEC 035 follow-up batch). These tests pin down the end-to-end contracts:
/// <list type="bullet">
///   <item>plaintext columns are encrypted via <see cref="EncryptedStringConverter"/>,</item>
///   <item>round-trip read returns the original plaintext,</item>
///   <item>equality lookups against the hash column resolve (the load-bearing case),</item>
///   <item>the cross-entity Solicitant→InsuredPerson join (Annex 6f) resolves through the
///         hash shadow columns,</item>
///   <item>the unique index on the hash column rejects duplicate plaintexts.</item>
/// </list>
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests were written alongside the wiring (TDD). They use
/// real <see cref="AesFieldEncryptor"/> and <see cref="Hmac256Hasher"/> instances bound to
/// fixed test keys — NEVER mocks — so the assertions exercise the actual canonicalization
/// and crypto paths the production system uses.
/// </remarks>
public class NationalIdHashShadowColumnTests
{
    /// <summary>Fixed test AES master key — 32 bytes.</summary>
    private static readonly byte[] TestEncryptionKey =
    [
        0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7,
        0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
        0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7,
        0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
    ];

    [Fact]
    public void NationalId_HasEncryptedConverter()
    {
        // Lock the wiring: NationalId on every applicable entity MUST be mapped with
        // EncryptedStringConverter, otherwise plaintext leaks to disk. The CnasDbContext
        // ctor that DOES NOT supply an encryptor skips the wiring — see EncryptedStringConverterTests.
        var encryptor = BuildEncryptor();
        using var db = BuildContext(NewDbName(), encryptor);

        AssertHasEncryptedConverter(db, typeof(Solicitant), nameof(Solicitant.NationalId));
        AssertHasEncryptedConverter(db, typeof(Contributor), nameof(Contributor.Idno));
        AssertHasEncryptedConverter(db, typeof(InsuredPerson), nameof(InsuredPerson.Idnp));
        AssertHasEncryptedConverter(db, typeof(UserProfile), nameof(UserProfile.NationalId));
    }

    [Fact]
    public void HashColumns_AreNotEncrypted()
    {
        // Hash columns are deterministic base64 — they MUST stay queryable, so the
        // encryption converter must NOT be applied to them.
        var encryptor = BuildEncryptor();
        using var db = BuildContext(NewDbName(), encryptor);

        AssertHasNoEncryptedConverter(db, typeof(Solicitant), nameof(Solicitant.NationalIdHash));
        AssertHasNoEncryptedConverter(db, typeof(Contributor), nameof(Contributor.IdnoHash));
        AssertHasNoEncryptedConverter(db, typeof(InsuredPerson), nameof(InsuredPerson.IdnpHash));
        AssertHasNoEncryptedConverter(db, typeof(UserProfile), nameof(UserProfile.NationalIdHash));
    }

    [Fact]
    public async Task Solicitant_NationalId_RoundTripsThroughEncryptedConverter()
    {
        // End-to-end: write a Solicitant with NationalId in plain form via the converter-aware
        // context, then reload through a fresh converter-aware context and verify the value
        // round-trips. This is the real-world flow service code uses.
        var dbName = NewDbName();
        var encryptor = BuildEncryptor();
        var hasher = IdHashHelper.Instance;
        const string idnp = "2000000000007";

        await using (var writeDb = BuildContext(dbName, encryptor))
        {
            writeDb.Solicitants.Add(new Solicitant
            {
                CreatedAtUtc = DateTime.UtcNow,
                NationalId = idnp,
                NationalIdHash = hasher.ComputeHash(idnp),
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Round-trip Test",
                PreferredLanguage = "ro",
                IsActive = true,
            });
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = BuildContext(dbName, encryptor);
        var loaded = readDb.Solicitants
            .AsNoTracking()
            .Single(s => s.NationalIdHash == hasher.ComputeHash(idnp));

        loaded.NationalId.Should().Be(idnp, "the converter must decrypt the column on read.");
    }

    [Fact]
    public async Task Solicitant_LookupByHash_FindsRowAfterConverterIsOn()
    {
        // The load-bearing test for ApplicationServiceImpl line 118 (MPower principal lookup):
        // with the converter active, the only way to find a Solicitant by IDNP is through
        // the NationalIdHash shadow column. Verify the lookup resolves end-to-end.
        var dbName = NewDbName();
        var encryptor = BuildEncryptor();
        var hasher = IdHashHelper.Instance;
        const string idnp = "1003600012346";

        await using (var writeDb = BuildContext(dbName, encryptor))
        {
            writeDb.Solicitants.Add(new Solicitant
            {
                CreatedAtUtc = DateTime.UtcNow,
                NationalId = idnp,
                NationalIdHash = hasher.ComputeHash(idnp),
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Lookup",
                PreferredLanguage = "ro",
                IsActive = true,
            });
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = BuildContext(dbName, encryptor);
        var idnpHash = hasher.ComputeHash(idnp);

        var found = await readDb.Solicitants
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.NationalIdHash == idnpHash && s.IsActive);

        found.Should().NotBeNull("the hash shadow column must resolve equality lookups even with encryption on.");
        found!.NationalId.Should().Be(idnp);
    }

    [Fact]
    public async Task Annex6fJoin_SolicitantToInsuredPerson_ResolvesViaHashColumns()
    {
        // The single most important contract in this batch: the Annex 6f
        // RPT-CASES-BY-AGE-GROUP report joins Solicitant→InsuredPerson on IDNP. With the
        // converter on, the only way the join resolves is through the *Hash shadow columns
        // (the plaintext encrypts to different ciphertext per row → join cannot match).
        var dbName = NewDbName();
        var encryptor = BuildEncryptor();
        var hasher = IdHashHelper.Instance;
        const string idnp = "2000000000007";
        var idnpHash = hasher.ComputeHash(idnp);

        await using (var writeDb = BuildContext(dbName, encryptor))
        {
            writeDb.Solicitants.Add(new Solicitant
            {
                CreatedAtUtc = DateTime.UtcNow,
                NationalId = idnp, NationalIdHash = idnpHash,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Join Test Beneficiary", PreferredLanguage = "ro", IsActive = true,
            });
            writeDb.InsuredPersons.Add(new InsuredPerson
            {
                CreatedAtUtc = DateTime.UtcNow,
                Idnp = idnp, IdnpHash = idnpHash,
                LastName = "Beneficiary", FirstName = "Test",
                BirthDate = new DateOnly(1970, 1, 1),
                RegisteredAtUtc = DateTime.UtcNow, IsActive = true,
            });
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = BuildContext(dbName, encryptor);
        var join = await (
            from s in readDb.Solicitants
            join ip in readDb.InsuredPersons on s.NationalIdHash equals ip.IdnpHash
            where ip.IsActive
            select new { s.NationalId, ip.Idnp, ip.BirthDate })
            .ToListAsync();

        join.Should().ContainSingle("the Solicitant→InsuredPerson hash-join must resolve to exactly one row.");
        join[0].NationalId.Should().Be(idnp);
        join[0].Idnp.Should().Be(idnp);
    }

    [Fact]
    public void Solicitant_UniqueIndex_IsOnHashColumnNotPlaintext()
    {
        // The unique index moved from NationalId to NationalIdHash so that "no two active
        // Solicitants share the same IDNP" stays enforceable even though the plaintext
        // column is now encrypted (different ciphertext per row → useless for index uniqueness).
        // We verify the model metadata directly because EF Core InMemory does not enforce
        // unique indexes at runtime — the contract here is a schema-shape contract that the
        // migration emits to Postgres.
        var encryptor = BuildEncryptor();
        using var db = BuildContext(NewDbName(), encryptor);

        var solicitantType = db.Model.FindEntityType(typeof(Solicitant))!;
        var indexes = solicitantType.GetIndexes().ToList();

        indexes.Should().Contain(
            idx => idx.IsUnique && idx.Properties.Count == 1 &&
                   idx.Properties[0].Name == nameof(Solicitant.NationalIdHash),
            "the unique-index moved to NationalIdHash so equality lookups still enforce uniqueness.");

        indexes.Should().NotContain(
            idx => idx.IsUnique && idx.Properties.Count == 1 &&
                   idx.Properties[0].Name == nameof(Solicitant.NationalId),
            "the encrypted plaintext column must NOT carry a unique index — it would be useless and bloated.");
    }

    [Theory]
    [InlineData(typeof(Contributor), nameof(Contributor.IdnoHash), nameof(Contributor.Idno))]
    [InlineData(typeof(InsuredPerson), nameof(InsuredPerson.IdnpHash), nameof(InsuredPerson.Idnp))]
    public void Entities_UniqueIndex_IsOnHashColumnNotPlaintext(Type entityType, string hashProp, string plaintextProp)
    {
        var encryptor = BuildEncryptor();
        using var db = BuildContext(NewDbName(), encryptor);

        var indexes = db.Model.FindEntityType(entityType)!.GetIndexes().ToList();

        indexes.Should().Contain(
            idx => idx.IsUnique && idx.Properties.Count == 1 && idx.Properties[0].Name == hashProp,
            $"{entityType.Name}.{hashProp} must carry the unique index for equality enforcement.");
        indexes.Should().NotContain(
            idx => idx.IsUnique && idx.Properties.Count == 1 && idx.Properties[0].Name == plaintextProp,
            $"{entityType.Name}.{plaintextProp} (encrypted) must NOT carry a unique index.");
    }

    /// <summary>Builds a real <see cref="AesFieldEncryptor"/> bound to <see cref="TestEncryptionKey"/>.</summary>
    private static IFieldEncryptor BuildEncryptor()
    {
        var opts = new FieldEncryptionOptions { Key = Convert.ToBase64String(TestEncryptionKey) };
        return new AesFieldEncryptor(Options.Create(opts));
    }

    /// <summary>Unique-per-test in-memory database name.</summary>
    private static string NewDbName() => $"cnas-nat-id-{Guid.NewGuid():N}";

    /// <summary>Builds a <see cref="CnasDbContext"/> bound to the named in-memory store and the given encryptor.</summary>
    private static CnasDbContext BuildContext(string dbName, IFieldEncryptor encryptor)
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts, encryptor);
    }

    /// <summary>Asserts that the named property on the given entity type is wired with EncryptedStringConverter.</summary>
    private static void AssertHasEncryptedConverter(CnasDbContext db, Type entityType, string propertyName)
    {
        var property = db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!;
        var converter = property.GetValueConverter();

        converter.Should().NotBeNull($"{entityType.Name}.{propertyName} must round-trip through EncryptedStringConverter.");
        converter.Should().BeOfType<EncryptedStringConverter>();
    }

    /// <summary>Asserts that the named property is NOT wired with any value converter.</summary>
    private static void AssertHasNoEncryptedConverter(CnasDbContext db, Type entityType, string propertyName)
    {
        var property = db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!;
        property.GetValueConverter().Should().BeNull(
            $"{entityType.Name}.{propertyName} is the deterministic hash shadow column — it must remain queryable, never encrypted.");
    }
}
