using System;
using System.Linq;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Security;

/// <summary>
/// Tests for <see cref="Hmac256Hasher"/> — the deterministic HMAC-SHA256
/// shadow-column hasher used to restore equality lookups on encrypted national
/// identifier columns (TOR SEC 035, CLAUDE.md §5.7 follow-up batch).
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the implementation. They
/// pin down the externally observable behaviour:
/// <list type="bullet">
///   <item>output is the base64 of an HMAC-SHA256 — fixed 44 chars including padding,</item>
///   <item>canonicalization rule (trim + ToUpperInvariant) is applied to every input,</item>
///   <item>output is deterministic: same input → same output forever,</item>
///   <item>output is key-sensitive: changing the salt changes every hash,</item>
///   <item>configuration validation rejects missing / short / non-base64 keys.</item>
/// </list>
/// </remarks>
public class Hmac256HasherTests
{
    /// <summary>32-byte (256-bit) deterministic salt — minimum recommended HMAC-SHA256 key length.</summary>
    private static readonly byte[] PrimaryKey =
    [
        0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47,
        0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
        0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57,
        0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
    ];

    /// <summary>An independent 32-byte salt used to prove key-sensitive output.</summary>
    private static readonly byte[] AlternateKey =
    [
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67,
        0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77,
        0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
    ];

    /// <summary>Convenience constructor — builds the SUT bound to the supplied raw key bytes.</summary>
    private static Hmac256Hasher BuildSut(byte[] key)
    {
        var opts = new FieldHashingOptions { SaltKey = Convert.ToBase64String(key) };
        return new Hmac256Hasher(Options.Create(opts));
    }

    [Fact]
    public void ComputeHash_SameInputSameKey_IsDeterministic()
    {
        var sut = BuildSut(PrimaryKey);
        const string input = "2000000000007";

        // 100 iterations: detect any non-determinism (per-instance state, time-based salt, ...).
        var hashes = Enumerable.Range(0, 100).Select(_ => sut.ComputeHash(input)).Distinct().ToArray();

        hashes.Should().ContainSingle(
            "HMAC-SHA256 over a fixed (key, message) tuple is mathematically deterministic — " +
            "any drift would indicate hidden per-call state.");
    }

    [Fact]
    public void ComputeHash_DifferentKeys_ProduceDifferentHashes()
    {
        // Key-sensitive output is the whole point of HMAC. A plain SHA-256 over the same
        // input would yield the same result regardless of key, defeating the brute-force
        // resistance this primitive exists to provide.
        var primary = BuildSut(PrimaryKey);
        var alternate = BuildSut(AlternateKey);

        var a = primary.ComputeHash("2000000000007");
        var b = alternate.ComputeHash("2000000000007");

        a.Should().NotBe(b,
            "HMAC keyed with different secrets MUST emit different hashes — otherwise rotating the " +
            "salt would be a no-op against an attacker who learned the old salt.");
    }

    [Fact]
    public void ComputeHash_Output_IsBase64SHA256SizedExactly()
    {
        var sut = BuildSut(PrimaryKey);

        var hash = sut.ComputeHash("2000000000007");

        // base64(32 bytes) = 44 chars including the two '=' padding characters. The shadow
        // column is declared VARCHAR(44); any drift here breaks the migration contract.
        hash.Should().HaveLength(44);
        // Confirm it round-trips as base64.
        var act = () => Convert.FromBase64String(hash);
        act.Should().NotThrow();
        Convert.FromBase64String(hash).Should().HaveCount(32);
    }

    [Theory]
    [InlineData("  2000000000007  ", "2000000000007")]          // surrounding whitespace
    [InlineData("\t2000000000007\n", "2000000000007")]         // tab + newline
    [InlineData("idnp-abcd", "IDNP-ABCD")]                       // case folding
    [InlineData("  iDnP-AbCd  ", "IDNP-ABCD")]                  // both
    public void ComputeHash_Canonicalization_TrimAndUppercase_ProduceSameHashAsCanonical(
        string raw, string canonical)
    {
        // The hasher must canonicalize BEFORE hashing so that semantically equal inputs
        // (e.g. an IDNP entered with leading space, or a mixed-case external code) collide
        // deterministically. Without this, `" 2000000000007 "` from an MConnect payload
        // would never match `"2000000000007"` in the local Solicitant table.
        var sut = BuildSut(PrimaryKey);

        var rawHash = sut.ComputeHash(raw);
        var canonicalHash = sut.ComputeHash(canonical);

        rawHash.Should().Be(canonicalHash,
            $"the hasher applies Trim + ToUpperInvariant to every input — '{raw}' should canonicalize to '{canonical}'.");
    }

    [Fact]
    public void ComputeHash_EmptyString_DoesNotThrow_AndReturnsStableValue()
    {
        // An empty string is a legitimate (if unusual) input — the empty-keyed HMAC is well
        // defined. The hasher must NOT throw on empty input; the caller (validators, services)
        // owns whether an empty national-id is acceptable.
        var sut = BuildSut(PrimaryKey);

        var a = sut.ComputeHash(string.Empty);
        var b = sut.ComputeHash(string.Empty);

        a.Should().HaveLength(44);
        a.Should().Be(b);
    }

    [Fact]
    public void ComputeHash_NullInput_Throws()
    {
        // Null is a programmer error, not a runtime user input. Surface it loudly.
        var sut = BuildSut(PrimaryKey);

        var act = () => sut.ComputeHash(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Construction_NullOptions_Throws()
    {
        var act = () => new Hmac256Hasher(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Construction_EmptySalt_Throws()
    {
        // Missing salt is a deployment misconfiguration that would otherwise quietly emit
        // an unsalted SHA256 — defeating the whole brute-force-resistance design.
        var opts = new FieldHashingOptions { SaltKey = string.Empty };

        var act = () => new Hmac256Hasher(Options.Create(opts));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Construction_NotBase64Salt_Throws()
    {
        var opts = new FieldHashingOptions { SaltKey = "not%%%base64$$$" };

        var act = () => new Hmac256Hasher(Options.Create(opts));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Construction_TooShortSalt_Throws()
    {
        // We require ≥ 32 bytes (NIST FIPS 198-1: HMAC key length should match the underlying
        // hash output length). Shorter keys reduce the brute-force search space.
        var shortKey = new byte[16];
        var opts = new FieldHashingOptions { SaltKey = Convert.ToBase64String(shortKey) };

        var act = () => new Hmac256Hasher(Options.Create(opts));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeHash_AcrossInstances_SameKeySameOutput()
    {
        // Process-restart resilience: same key bytes on a fresh instance must yield the
        // same hash for the same input. Anything else implies hidden per-instance state.
        var first = BuildSut(PrimaryKey);
        var second = BuildSut(PrimaryKey);

        first.ComputeHash("1003600012346").Should().Be(second.ComputeHash("1003600012346"));
    }

    [Fact]
    public void Implements_IDeterministicHasher()
    {
        // Lock the contract — DI registers Hmac256Hasher behind the IDeterministicHasher
        // abstraction; downstream code (services, EF configurations) must never reference
        // the concrete type.
        var sut = BuildSut(PrimaryKey);
        sut.Should().BeAssignableTo<IDeterministicHasher>();
    }
}
