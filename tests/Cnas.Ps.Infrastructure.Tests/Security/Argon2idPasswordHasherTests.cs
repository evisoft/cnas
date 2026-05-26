using System;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure.Security;
using FluentAssertions;

namespace Cnas.Ps.Infrastructure.Tests.Security;

/// <summary>
/// Unit tests for <see cref="Argon2idPasswordHasher"/> — the Argon2id implementation of
/// <see cref="IPasswordHasher"/> used by the local-login fallback (R0051, R0052).
/// </summary>
/// <remarks>
/// <para>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the implementation. They pin
/// down the externally observable behaviour:
/// </para>
/// <list type="bullet">
///   <item>The PHC string carries the canonical OWASP 2024 parameter block.</item>
///   <item>A random salt is generated per call → identical plaintexts yield different hashes.</item>
///   <item>Verification round-trips successfully against the salt embedded in the PHC string.</item>
///   <item>Verification rejects mismatched plaintexts in constant time.</item>
///   <item>Malformed PHC strings produce <c>false</c> — Verify never throws.</item>
///   <item>Tampered hash bytes are detected via the constant-time comparison.</item>
/// </list>
/// </remarks>
public class Argon2idPasswordHasherTests
{
    /// <summary>
    /// Canonical compliant password used across positive-path tests. Placeholder only —
    /// per the batch brief: "No PII in test fixtures."
    /// </summary>
    private const string ValidPassword = "Aa1!aaaa";

    /// <summary>
    /// Expected leading parameter block in every PHC string produced by the hasher.
    /// Matches OWASP 2024 Argon2id recommendation: 64 MiB / 4 iterations / 4 parallelism.
    /// </summary>
    private const string ExpectedPhcPrefix = "$argon2id$v=19$m=65536,t=4,p=4$";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Hash_NullOrWhitespace_Throws(string? plaintext)
    {
        // Null / whitespace plaintext is a programmer error — caller must validate first.
        var sut = new Argon2idPasswordHasher();

        var act = () => sut.Hash(plaintext!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Hash_ValidPassword_ReturnsPhcFormat()
    {
        // The PHC string must start with the canonical parameter block and carry the
        // base64(salt) and base64(hash) segments — exactly five $-delimited segments
        // after the leading empty segment (segments: "", "argon2id", "v=19",
        // "m=65536,t=4,p=4", "<saltB64>", "<hashB64>").
        var sut = new Argon2idPasswordHasher();

        var phc = sut.Hash(ValidPassword);

        phc.Should().StartWith(ExpectedPhcPrefix);

        var segments = phc.Split('$');
        segments.Should().HaveCount(6,
            "PHC format is `$argon2id$v=19$m=65536,t=4,p=4$<saltB64>$<hashB64>` which has six $-delimited segments.");
        segments[0].Should().BeEmpty("the string starts with '$'.");
        segments[4].Should().NotBeNullOrEmpty();
        segments[5].Should().NotBeNullOrEmpty();

        // Salt round-trips as base64 to exactly 16 bytes.
        var saltBytes = Convert.FromBase64String(segments[4]);
        saltBytes.Should().HaveCount(16);

        // Hash round-trips as base64 to exactly 32 bytes.
        var hashBytes = Convert.FromBase64String(segments[5]);
        hashBytes.Should().HaveCount(32);
    }

    [Fact]
    public void Hash_SamePassword_TwiceProducesDifferentHashes()
    {
        // The salt MUST be randomly generated per call — otherwise two users with the
        // same password would share the same hash, defeating per-row salt protection.
        var sut = new Argon2idPasswordHasher();

        var first = sut.Hash(ValidPassword);
        var second = sut.Hash(ValidPassword);

        first.Should().NotBe(second,
            "the per-call random salt must make repeat hashes diverge — otherwise " +
            "password reuse across accounts would be observable from the hash column.");
    }

    [Fact]
    public void Verify_MatchingPlaintext_ReturnsTrue()
    {
        // Positive path: hash then immediately verify with the same plaintext.
        var sut = new Argon2idPasswordHasher();
        var phc = sut.Hash(ValidPassword);

        var ok = sut.Verify(ValidPassword, phc);

        ok.Should().BeTrue(
            "the PHC string carries the salt + parameters needed for deterministic re-derivation.");
    }

    [Fact]
    public void Verify_NonMatchingPlaintext_ReturnsFalse()
    {
        // Negative path: one character off → must fail. The constant-time comparison
        // ensures no timing leak based on which byte differs.
        var sut = new Argon2idPasswordHasher();
        var phc = sut.Hash(ValidPassword);

        var ok = sut.Verify("Aa1!aaab", phc);

        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("not-a-phc-string")]
    [InlineData("$argon2id$v=19$")]
    [InlineData("$argon2id$v=19$m=65536,t=4,p=4$invalid-base64$invalid-base64")]
    [InlineData("$argon2i$v=19$m=65536,t=4,p=4$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("")]
    [InlineData(null)]
    public void Verify_MalformedPhc_ReturnsFalse(string? phc)
    {
        // Contract: Verify NEVER throws. Any malformed input — wrong algorithm,
        // garbage segments, missing parts, null, empty — produces false.
        // This protects callers from having to defensively try/catch on every login.
        var sut = new Argon2idPasswordHasher();

        var ok = sut.Verify(ValidPassword, phc!);

        ok.Should().BeFalse(
            "the contract guarantees Verify returns false (never throws) on malformed PHC strings.");
    }

    [Fact]
    public void Verify_TamperedHash_ReturnsFalse()
    {
        // Flip the last character of the base64-encoded hash and confirm the
        // constant-time comparison rejects the result. We pick the last meaningful
        // character (skipping '=' padding) so the change cannot be absorbed by padding
        // normalization.
        var sut = new Argon2idPasswordHasher();
        var phc = sut.Hash(ValidPassword);

        var segments = phc.Split('$');
        var hashB64 = segments[5];

        // Find the last non-padding character.
        var idx = hashB64.Length - 1;
        while (idx >= 0 && hashB64[idx] == '=')
        {
            idx--;
        }
        idx.Should().BeGreaterThanOrEqualTo(0);

        // Flip to a deterministically-different base64 character.
        var original = hashB64[idx];
        var replacement = original == 'A' ? 'B' : 'A';
        var tampered = hashB64.Substring(0, idx) + replacement + hashB64.Substring(idx + 1);
        segments[5] = tampered;
        var tamperedPhc = string.Join('$', segments);

        var ok = sut.Verify(ValidPassword, tamperedPhc);

        ok.Should().BeFalse(
            "tampering with even one byte of the hash must be caught by FixedTimeEquals.");
    }

    [Fact]
    public void Implements_IPasswordHasher()
    {
        // Lock the contract — DI registers Argon2idPasswordHasher behind the
        // IPasswordHasher abstraction; downstream code must never reference the concrete.
        var sut = new Argon2idPasswordHasher();
        sut.Should().BeAssignableTo<IPasswordHasher>();
    }
}
