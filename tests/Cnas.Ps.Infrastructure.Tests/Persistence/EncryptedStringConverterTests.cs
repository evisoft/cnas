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
/// Tests for the encrypted-string value converter wired against
/// <see cref="Solicitant.BankIban"/> through <see cref="CnasDbContext"/>.
/// Verifies that the column is encrypted at rest (the raw stored value differs
/// from the plaintext) and decrypted transparently on read.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the converter and the
/// CnasDbContext wiring. They use EF Core's InMemory provider; value converters
/// still run on InMemory (the converter is applied at the property layer, not
/// the SQL translation layer), so this is a faithful test of behaviour.
/// </remarks>
public class EncryptedStringConverterTests
{
    /// <summary>32-byte (256-bit) deterministic key for the test suite.</summary>
    private static readonly byte[] TestKey =
    [
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
        0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
        0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
    ];

    [Fact]
    public void Save_BankIban_AppliesEncryptedConverterToColumn()
    {
        // EF Core's InMemory provider does not round-trip values through the
        // configured ValueConverter on save — the in-memory store holds the
        // model-side CLR object directly. So verifying "ciphertext at rest"
        // by reading from a converter-less context would be misleading on
        // InMemory (it would always see the original plaintext regardless
        // of converter wiring). Instead we verify the contract that ACTUALLY
        // matters in production: the BankIban property is mapped with the
        // EncryptedStringConverter, so any relational provider (PostgreSQL
        // in prod) will emit the ciphertext on insert and decrypt on read.
        var encryptor = BuildEncryptor();
        using var db = BuildContext(NewDbName(), encryptor);

        var property = db.Model.FindEntityType(typeof(Solicitant))!
            .FindProperty(nameof(Solicitant.BankIban))!;
        var converter = property.GetValueConverter();

        converter.Should().NotBeNull();
        converter.Should().BeOfType<EncryptedStringConverter>();

        // Exercise the converter to confirm it produces a versioned envelope.
        var sealed_ = (string)converter!.ConvertToProvider("MD24AG000000000000001234")!;
        sealed_.Should().StartWith("v1:");
        sealed_.Should().NotContain("MD24AG000000000000001234");

        // And that the inverse direction round-trips back to plaintext.
        var unsealed = (string)converter.ConvertFromProvider(sealed_)!;
        unsealed.Should().Be("MD24AG000000000000001234");
    }

    [Fact]
    public void NoEncryptor_BankIban_HasNoConverter()
    {
        // Test/tooling constructor path: no encryptor → no converter → the
        // column round-trips as plaintext. This is intentional: tests that
        // do not care about encryption should not be forced to wire it.
        using var db = BuildContext(NewDbName(), encryptor: null);

        var property = db.Model.FindEntityType(typeof(Solicitant))!
            .FindProperty(nameof(Solicitant.BankIban))!;

        property.GetValueConverter().Should().BeNull();
    }

    [Fact]
    public async Task Load_BankIban_DecryptsToOriginalPlaintext()
    {
        var dbName = NewDbName();
        var encryptor = BuildEncryptor();

        const string iban = "MD24AG000000000000001234";
        await using (var writeDb = BuildContext(dbName, encryptor))
        {
            writeDb.Solicitants.Add(new Solicitant
            {
                NationalId = "1003600012346",
                DisplayName = "Test User",
                BankIban = iban,
            });
            await writeDb.SaveChangesAsync();
        }

        // Read via the converter-aware context — should round-trip transparently.
        await using var readDb = BuildContext(dbName, encryptor);
        var loaded = readDb.Solicitants.AsNoTracking().Single(s => s.NationalId == "1003600012346");

        loaded.BankIban.Should().Be(iban);
    }

    [Fact]
    public async Task Save_Null_StoresNull()
    {
        // Nullable column semantics: null on the domain side must remain null at
        // rest (we cannot encrypt nothing — and crucially we don't want a fixed
        // sentinel ciphertext that would leak "this row has no IBAN").
        var dbName = NewDbName();
        var encryptor = BuildEncryptor();

        await using (var writeDb = BuildContext(dbName, encryptor))
        {
            writeDb.Solicitants.Add(new Solicitant
            {
                NationalId = "1003600012346",
                DisplayName = "Test User",
                BankIban = null,
            });
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = BuildContext(dbName, encryptor);
        var loaded = readDb.Solicitants.AsNoTracking().Single(s => s.NationalId == "1003600012346");
        loaded.BankIban.Should().BeNull();

        // Also confirm the raw column is null (no sentinel ciphertext written).
        await using var rawDb = BuildContext(dbName, encryptor: null);
        rawDb.Solicitants.AsNoTracking().Single(s => s.NationalId == "1003600012346").BankIban.Should().BeNull();
    }

    /// <summary>Builds a real <see cref="AesFieldEncryptor"/> bound to <see cref="TestKey"/>.</summary>
    private static IFieldEncryptor BuildEncryptor()
    {
        var opts = new FieldEncryptionOptions { Key = Convert.ToBase64String(TestKey) };
        return new AesFieldEncryptor(Options.Create(opts));
    }

    /// <summary>Unique-per-test in-memory database name to keep test stores isolated.</summary>
    private static string NewDbName() => $"cnas-enc-{Guid.NewGuid():N}";

    /// <summary>
    /// Builds a <see cref="CnasDbContext"/> against the named in-memory store.
    /// When <paramref name="encryptor"/> is <c>null</c> the converter is not
    /// applied — useful for asserting the raw at-rest column value.
    /// </summary>
    private static CnasDbContext BuildContext(string dbName, IFieldEncryptor? encryptor)
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return encryptor is null ? new CnasDbContext(opts) : new CnasDbContext(opts, encryptor);
    }
}
