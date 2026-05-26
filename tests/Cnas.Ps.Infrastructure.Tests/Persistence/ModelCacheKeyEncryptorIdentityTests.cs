using System;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Persistence;

/// <summary>
/// Regression tests for <see cref="CnasModelCacheKeyFactory"/> proving that the EF
/// Core compiled-model cache key discriminates on the concrete
/// <see cref="IFieldEncryptor"/> identity, not merely on whether <i>some</i> encryptor
/// is wired (BUG-003 from milestone #79).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this matters.</b> EF Core caches the result of <c>OnModelCreating</c>
/// keyed by the object returned from <see cref="IModelCacheKeyFactory.Create"/>.
/// The previous implementation discriminated only on <c>bool HasFieldEncryptor</c>,
/// so two contexts in the same process that wired DIFFERENT encryptor
/// implementations (e.g. <see cref="AesFieldEncryptor"/> in one fixture and
/// <see cref="MissingKeyFieldEncryptor"/> in another) collided on the same key
/// and inherited whichever model was built first. The visible symptom was
/// wrong-converter wiring at <c>SaveChanges</c> time once both fixtures booted
/// in parallel. The fix: include the encryptor's CLR type in the key tuple.
/// </para>
/// <para>
/// Per CLAUDE.md RULE 1 these tests were written before the implementation
/// changed: this file went red against the old <c>bool</c>-only discriminator
/// and turned green only after the factory was updated.
/// </para>
/// </remarks>
public class ModelCacheKeyEncryptorIdentityTests
{
    /// <summary>32-byte (256-bit) deterministic key used to construct real AES encryptors.</summary>
    private static readonly byte[] TestKey =
    [
        0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47,
        0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
        0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57,
        0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
    ];

    /// <summary>A second, distinct 32-byte key — proves identity is type-keyed, not key-keyed.</summary>
    private static readonly byte[] TestKeyAlt =
    [
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67,
        0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77,
        0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
    ];

    /// <summary>
    /// Core BUG-003 assertion: contexts wired with distinct <see cref="IFieldEncryptor"/>
    /// implementations MUST produce distinct cache keys so EF compiles independent
    /// models for each (one with the BankIban / NationalId converter wired, the other
    /// throwing on first use of an encrypted column).
    /// </summary>
    [Fact]
    public void Create_TwoDifferentEncryptorTypes_ProducesDifferentCacheKeys()
    {
        using var aesCtx = BuildContext(NewDbName(), BuildAesEncryptor(TestKey));
        using var missingCtx = BuildContext(NewDbName(), new MissingKeyFieldEncryptor());

        var factory = new CnasModelCacheKeyFactory();
        var aesKey = factory.Create(aesCtx, designTime: false);
        var missingKey = factory.Create(missingCtx, designTime: false);

        aesKey.Should().NotBe(missingKey,
            "AES and Missing-key encryptors must compile to different EF models — " +
            "the AES one wires the EncryptedStringConverter, the Missing-key one does not.");
    }

    /// <summary>
    /// Same encryptor TYPE on both contexts must collapse to the same cache key so
    /// EF reuses a single compiled model — this is the whole reason a cache exists.
    /// The actual key bytes differ between the two encryptors to make absolutely
    /// sure the discriminator is the CLR type, not the encryptor instance / state.
    /// </summary>
    [Fact]
    public void Create_SameEncryptorType_ProducesEqualCacheKeys()
    {
        using var ctxA = BuildContext(NewDbName(), BuildAesEncryptor(TestKey));
        using var ctxB = BuildContext(NewDbName(), BuildAesEncryptor(TestKeyAlt));

        var factory = new CnasModelCacheKeyFactory();
        var keyA = factory.Create(ctxA, designTime: false);
        var keyB = factory.Create(ctxB, designTime: false);

        keyA.Should().Be(keyB,
            "two AesFieldEncryptor-backed contexts must share a compiled model regardless of key bytes.");
        keyA.GetHashCode().Should().Be(keyB.GetHashCode(),
            "structural-equality records must agree on hash code when Equal returns true.");
    }

    /// <summary>
    /// Two encryptor-less contexts (built via the single-arg ctor used by EF tooling
    /// and unit tests) must share a single cache key. The "no encryptor" sentinel
    /// must also be DISTINCT from any encryptor-enabled key so the converter wiring
    /// does not leak between tooling and runtime models.
    /// </summary>
    [Fact]
    public void Create_NoEncryptor_ProducesStableCacheKey()
    {
        using var ctxA = BuildContext(NewDbName(), encryptor: null);
        using var ctxB = BuildContext(NewDbName(), encryptor: null);
        using var ctxAes = BuildContext(NewDbName(), BuildAesEncryptor(TestKey));

        var factory = new CnasModelCacheKeyFactory();
        var keyA = factory.Create(ctxA, designTime: false);
        var keyB = factory.Create(ctxB, designTime: false);
        var keyAes = factory.Create(ctxAes, designTime: false);

        keyA.Should().Be(keyB,
            "two encryptor-less contexts must reuse the same compiled model.");
        keyA.Should().NotBe(keyAes,
            "the encryptor-less key must remain distinct from any encryptor-enabled key — " +
            "BUG-003 would otherwise repeat the moment a non-AES encryptor enters the process.");
    }

    /// <summary>Constructs a real <see cref="AesFieldEncryptor"/> bound to the given raw key bytes.</summary>
    private static IFieldEncryptor BuildAesEncryptor(byte[] keyBytes)
    {
        var opts = new FieldEncryptionOptions { Key = Convert.ToBase64String(keyBytes) };
        return new AesFieldEncryptor(Options.Create(opts));
    }

    /// <summary>Unique-per-test in-memory database name so contexts do not share state across tests.</summary>
    private static string NewDbName() => $"cnas-cachekey-{Guid.NewGuid():N}";

    /// <summary>
    /// Builds a <see cref="CnasDbContext"/> against the named in-memory store, using
    /// the single-arg ctor when <paramref name="encryptor"/> is <c>null</c> and the
    /// DI-style ctor otherwise. Mirrors <c>EncryptedStringConverterTests.BuildContext</c>.
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
