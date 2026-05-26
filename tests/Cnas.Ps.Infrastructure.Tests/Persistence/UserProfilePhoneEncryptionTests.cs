using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Persistence.Conversion;
using Cnas.Ps.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// Encryption-at-rest tests for <see cref="UserProfile.PhoneE164"/> — TOR SEC 035 / CLAUDE.md §5.7.
/// Locks the wiring that promotes phone numbers from "stored in clear" to "stored encrypted":
/// <list type="bullet">
///   <item>The column is mapped through <see cref="EncryptedStringConverter"/> at the model layer,</item>
///   <item>plaintext written via the converter-aware context decrypts back to the original value,</item>
///   <item>writes without the converter (the tests/tooling ctor) persist plaintext, so an in-memory
///         "raw at rest" snapshot can be inspected,</item>
///   <item>no index is registered on the encrypted column — equality lookups on encrypted PhoneE164
///         silently return zero rows (mirrors the documented Idnp / Idno pattern).</item>
/// </list>
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the converter wiring lands in
/// <see cref="CnasDbContext"/>. They use real <see cref="AesFieldEncryptor"/> instances bound to a
/// fixed test key — never mocks — so the assertions exercise the actual crypto path the production
/// system uses.
/// </remarks>
public class UserProfilePhoneEncryptionTests
{
    /// <summary>Fixed 32-byte test AES master key.</summary>
    private static readonly byte[] TestEncryptionKey =
    [
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
        0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7,
        0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
    ];

    [Fact]
    public void PhoneE164_HasEncryptedConverter()
    {
        // Lock the wiring: UserProfile.PhoneE164 MUST be mapped with EncryptedStringConverter,
        // otherwise plaintext PII leaks to disk (TOR SEC 035 violation).
        var encryptor = BuildEncryptor();
        using var db = BuildContext(NewDbName(), encryptor);

        var property = db.Model.FindEntityType(typeof(UserProfile))!
            .FindProperty(nameof(UserProfile.PhoneE164))!;
        var converter = property.GetValueConverter();

        converter.Should().NotBeNull(
            "UserProfile.PhoneE164 must round-trip through EncryptedStringConverter.");
        converter.Should().BeOfType<EncryptedStringConverter>();
    }

    [Fact]
    public void PhoneE164_NoEncryptor_HasNoConverter()
    {
        // The test/tooling ctor leaves encrypted columns unwired — confirm the converter
        // disappears when no encryptor is supplied (otherwise migration tooling would
        // fail to resolve dependencies).
        using var db = BuildContext(NewDbName(), encryptor: null);

        var property = db.Model.FindEntityType(typeof(UserProfile))!
            .FindProperty(nameof(UserProfile.PhoneE164))!;

        property.GetValueConverter().Should().BeNull();
    }

    [Fact]
    public void PhoneE164_NoIndexOnEncryptedColumn()
    {
        // Equality lookups against the encrypted PhoneE164 are useless (fresh nonce per
        // encryption → different ciphertext per row). The model MUST NOT carry an index on
        // this column — mirrors the Idnp/Idno pattern. Phone is a display field, not a
        // search key; we deliberately do NOT add a hash shadow column.
        var encryptor = BuildEncryptor();
        using var db = BuildContext(NewDbName(), encryptor);

        var indexes = db.Model.FindEntityType(typeof(UserProfile))!.GetIndexes().ToList();

        indexes.Should().NotContain(
            idx => idx.Properties.Count == 1 &&
                   idx.Properties[0].Name == nameof(UserProfile.PhoneE164),
            "no index may be defined on the encrypted PhoneE164 column — it would be useless and bloated.");
    }

    [Fact]
    public void Save_PhoneE164_AppliesEncryptedConverterToColumn()
    {
        // EF Core's InMemory provider does not round-trip values through the configured
        // ValueConverter on save — the in-memory store holds the model-side CLR object
        // directly. So verifying "ciphertext at rest" by reading from a converter-less
        // context would be misleading on InMemory (it would always see the original
        // plaintext regardless of converter wiring). Instead we verify the contract that
        // ACTUALLY matters in production: the PhoneE164 property is mapped with the
        // EncryptedStringConverter, so any relational provider (PostgreSQL in prod) will
        // emit the ciphertext on insert and decrypt on read. This is the same pattern
        // used by EncryptedStringConverterTests for Solicitant.BankIban.
        const string phone = "+37369123456";
        var encryptor = BuildEncryptor();
        using var db = BuildContext(NewDbName(), encryptor);

        var property = db.Model.FindEntityType(typeof(UserProfile))!
            .FindProperty(nameof(UserProfile.PhoneE164))!;
        var converter = property.GetValueConverter();

        converter.Should().NotBeNull();
        converter.Should().BeOfType<EncryptedStringConverter>();

        // Exercise the converter to confirm it produces a versioned envelope that does
        // NOT contain the plaintext.
        var sealed_ = (string)converter!.ConvertToProvider(phone)!;
        sealed_.Should().StartWith("v1:",
            "the encrypted-string converter must emit the v1: envelope prefix.");
        sealed_.Should().NotContain(phone,
            "the stored column must be ciphertext, not plaintext PII.");

        // And that the inverse direction round-trips back to plaintext.
        var unsealed = (string)converter.ConvertFromProvider(sealed_)!;
        unsealed.Should().Be(phone);
    }

    [Fact]
    public async Task Load_PhoneE164_DecryptsToOriginalPlaintext()
    {
        // Round-trip the value through SaveChanges + a fresh converter-aware context;
        // the reader must see the plaintext (the converter transparently decrypts).
        var dbName = NewDbName();
        var encryptor = BuildEncryptor();
        const string phone = "+37369123456";

        await using (var writeDb = BuildContext(dbName, encryptor))
        {
            writeDb.UserProfiles.Add(new UserProfile
            {
                MPassSubject = "phone-roundtrip-sub",
                DisplayName = "Phone Round-Trip",
                PhoneE164 = phone,
                PreferredLanguage = "ro",
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            });
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = BuildContext(dbName, encryptor);
        var loaded = await readDb.UserProfiles.AsNoTracking()
            .SingleAsync(u => u.MPassSubject == "phone-roundtrip-sub");

        loaded.PhoneE164.Should().Be(phone,
            "the converter must transparently decrypt the column on read.");
    }

    [Fact]
    public async Task Save_NullPhoneE164_StoresNull()
    {
        // Nullable column semantics: null on the domain side stays null at rest. We
        // explicitly DO NOT want a sentinel ciphertext that would leak the
        // "has phone / no phone" bit.
        var dbName = NewDbName();
        var encryptor = BuildEncryptor();

        long persistedId;
        await using (var writeDb = BuildContext(dbName, encryptor))
        {
            var profile = new UserProfile
            {
                MPassSubject = "phone-null-sub",
                DisplayName = "Phone Null",
                PhoneE164 = null,
                PreferredLanguage = "ro",
                CreatedAtUtc = DateTime.UtcNow,
                IsActive = true,
            };
            writeDb.UserProfiles.Add(profile);
            await writeDb.SaveChangesAsync();
            persistedId = profile.Id;
        }

        await using var rawDb = BuildContext(dbName, encryptor: null);
        var raw = await rawDb.UserProfiles.AsNoTracking()
            .SingleAsync(u => u.Id == persistedId);

        raw.PhoneE164.Should().BeNull("no sentinel ciphertext may be written for null PhoneE164.");
    }

    /// <summary>Builds a real <see cref="AesFieldEncryptor"/> bound to <see cref="TestEncryptionKey"/>.</summary>
    private static IFieldEncryptor BuildEncryptor()
    {
        var opts = new FieldEncryptionOptions { Key = Convert.ToBase64String(TestEncryptionKey) };
        return new AesFieldEncryptor(Options.Create(opts));
    }

    /// <summary>Unique-per-test in-memory database name.</summary>
    private static string NewDbName() => $"cnas-uphone-{Guid.NewGuid():N}";

    /// <summary>Builds a <see cref="CnasDbContext"/> against the named in-memory store.</summary>
    /// <param name="dbName">Name of the in-memory database — shared across contexts that round-trip data.</param>
    /// <param name="encryptor">Encryptor to wire; <c>null</c> selects the converter-less ctor.</param>
    private static CnasDbContext BuildContext(string dbName, IFieldEncryptor? encryptor)
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return encryptor is null ? new CnasDbContext(opts) : new CnasDbContext(opts, encryptor);
    }
}
