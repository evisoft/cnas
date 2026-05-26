using System;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Security;

/// <summary>
/// Tests for <see cref="AesFieldEncryptor"/> — the AES-256-GCM application-level
/// field encryptor used to protect highly confidential columns at rest per
/// CLAUDE.md §5.7 (Application-Level Encryption) and TOR SEC 035.
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the implementation. They
/// pin down the externally observable behaviour: envelope format, nonce uniqueness,
/// tamper resistance (via the GCM tag), key-length validation, and version
/// gating that enables future v2 key rotation.
/// </remarks>
public class AesFieldEncryptorTests
{
    /// <summary>32-byte (256-bit) AES key — the only key length AES-GCM accepts here.</summary>
    private static readonly byte[] ValidKey =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
    ];

    /// <summary>Convenience: build the SUT bound to <see cref="ValidKey"/>.</summary>
    private static AesFieldEncryptor BuildSut()
    {
        var opts = new FieldEncryptionOptions { Key = Convert.ToBase64String(ValidKey) };
        return new AesFieldEncryptor(Options.Create(opts));
    }

    [Fact]
    public void Encrypt_RoundTrip_ReturnsOriginalPlaintext()
    {
        var sut = BuildSut();
        const string plaintext = "MD24AG000000000000001234";

        var ciphertext = sut.Encrypt(plaintext);
        var decrypted = sut.Decrypt(ciphertext);

        decrypted.Should().Be(plaintext);
        ciphertext.Should().StartWith("v1:");
        ciphertext.Should().NotContain(plaintext);
    }

    [Fact]
    public void Encrypt_TwoCalls_ProduceDifferentCiphertext()
    {
        // GCM is catastrophic on nonce reuse (key recovery is possible from two
        // ciphertexts encrypted under the same (key, nonce) pair). Two encrypts
        // of the same plaintext MUST produce different envelopes — proves the
        // implementation samples a fresh random nonce per call.
        var sut = BuildSut();
        const string plaintext = "MD24AG000000000000001234";

        var first = sut.Encrypt(plaintext);
        var second = sut.Encrypt(plaintext);

        first.Should().NotBe(second);
        sut.Decrypt(first).Should().Be(plaintext);
        sut.Decrypt(second).Should().Be(plaintext);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsFieldDecryptionException()
    {
        // Flip a byte in the middle of the envelope payload — the GCM auth tag
        // verification must reject it. This is the primary integrity guarantee
        // of AES-GCM (vs CBC, which would silently decrypt corrupt data).
        var sut = BuildSut();
        var envelope = sut.Encrypt("MD24AG000000000000001234");
        var tampered = TamperByteAt(envelope, position: 4);

        var act = () => sut.Decrypt(tampered);

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_TamperedTag_ThrowsFieldDecryptionException()
    {
        // Flip the very last byte (the trailing byte of the 16-byte GCM tag) —
        // even a single-bit flip in the tag invalidates the entire envelope.
        var sut = BuildSut();
        var envelope = sut.Encrypt("MD24AG000000000000001234");
        var lastBase64Char = envelope[^1];
        // Flip the last base64 char to a different valid one (preserves base64
        // shape but corrupts the underlying tag byte).
        var replacement = lastBase64Char == 'A' ? 'B' : 'A';
        var tampered = envelope[..^1] + replacement;

        var act = () => sut.Decrypt(tampered);

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_UnknownVersion_ThrowsFieldDecryptionException()
    {
        // v9: is reserved for a future migration we haven't implemented; the
        // current code must refuse rather than silently treat it as v1.
        var sut = BuildSut();

        var act = () => sut.Decrypt("v9:" + Convert.ToBase64String(new byte[40]));

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_NoVersionPrefix_ThrowsFieldDecryptionException()
    {
        // Bare base64 (no "vN:" prefix) is rejected — versioning is mandatory.
        var sut = BuildSut();

        var act = () => sut.Decrypt(Convert.ToBase64String(new byte[40]));

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_NotBase64_ThrowsFieldDecryptionException()
    {
        var sut = BuildSut();

        var act = () => sut.Decrypt("v1:not-base64-@@@");

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_TooShortPayload_ThrowsFieldDecryptionException()
    {
        // Five bytes can't contain even a nonce (12) + tag (16), let alone any
        // ciphertext. Must be rejected before we hand it to AesGcm.
        var sut = BuildSut();
        var tooShort = "v1:" + Convert.ToBase64String(new byte[5]);

        var act = () => sut.Decrypt(tooShort);

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_Null_ThrowsFieldDecryptionException()
    {
        var sut = BuildSut();

        var act = () => sut.Decrypt(null!);

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Decrypt_Empty_ThrowsFieldDecryptionException()
    {
        var sut = BuildSut();

        var act = () => sut.Decrypt(string.Empty);

        act.Should().Throw<FieldDecryptionException>();
    }

    [Fact]
    public void Constructor_WrongKeyLength_ThrowsArgumentException()
    {
        // 16 bytes (AES-128 key length) is rejected — we mandate 256-bit keys
        // for confidential data per TOR SEC 035.
        var opts = new FieldEncryptionOptions { Key = Convert.ToBase64String(new byte[16]) };

        var act = () => _ = new AesFieldEncryptor(Options.Create(opts));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullKey_ThrowsArgumentException()
    {
        var opts = new FieldEncryptionOptions { Key = null! };

        var act = () => _ = new AesFieldEncryptor(Options.Create(opts));

        // Either ArgumentNullException or ArgumentException is acceptable —
        // both inherit ArgumentException.
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_EmptyKey_ThrowsArgumentException()
    {
        var opts = new FieldEncryptionOptions { Key = string.Empty };

        var act = () => _ = new AesFieldEncryptor(Options.Create(opts));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NonBase64Key_ThrowsArgumentException()
    {
        var opts = new FieldEncryptionOptions { Key = "@@@not-base64@@@" };

        var act = () => _ = new AesFieldEncryptor(Options.Create(opts));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Encrypt_EmptyString_RoundTrips()
    {
        // Boundary: empty plaintext is a legitimate value (a column set to "").
        var sut = BuildSut();

        var ciphertext = sut.Encrypt(string.Empty);
        var decrypted = sut.Decrypt(ciphertext);

        decrypted.Should().Be(string.Empty);
        ciphertext.Should().StartWith("v1:");
    }

    [Fact]
    public void Encrypt_UnicodePlaintext_RoundTrips()
    {
        // Romanian + Cyrillic diacritics — the encoder must use UTF-8 so the
        // ciphertext round-trips losslessly.
        var sut = BuildSut();
        const string plaintext = "Ștefan cel Mare — пенсия";

        var decrypted = sut.Decrypt(sut.Encrypt(plaintext));

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_NullPlaintext_ThrowsArgumentNullException()
    {
        // Null plaintext is a programmer error — the EF converter is responsible
        // for short-circuiting nullable columns BEFORE calling Encrypt.
        var sut = BuildSut();

        var act = () => sut.Encrypt(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Flips one byte in the base64-encoded payload portion of a versioned
    /// envelope, preserving the <c>v1:</c> prefix.
    /// </summary>
    private static string TamperByteAt(string envelope, int position)
    {
        var prefixEnd = envelope.IndexOf(':', StringComparison.Ordinal) + 1;
        var prefix = envelope[..prefixEnd];
        var payload = Convert.FromBase64String(envelope[prefixEnd..]);
        payload[position] ^= 0xFF;
        return prefix + Convert.ToBase64String(payload);
    }
}
