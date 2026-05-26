using System;
using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Konscious.Security.Cryptography;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// Argon2id implementation of <see cref="IPasswordHasher"/>. Produces and verifies
/// PHC-formatted strings of the shape
/// <c>$argon2id$v=19$m=65536,t=4,p=4$&lt;base64salt&gt;$&lt;base64hash&gt;</c>.
/// Parameters follow OWASP 2024 Argon2id guidance: 64 MiB memory, 4 iterations,
/// 4 parallel lanes, 16-byte random salt, 32-byte hash output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Argon2id (vs bcrypt / PBKDF2).</b> Argon2id is the only memory-hard KDF
/// recommended by both OWASP (2024 cheat sheet) and the IETF (RFC 9106) as the
/// modern default. The memory-hardness component frustrates GPU/ASIC attackers in
/// ways pure compute-hardness (bcrypt, PBKDF2) cannot. Used here for the local
/// <c>Utilizator autorizat</c> credential fallback only — citizens authenticate
/// via MPass SAML and never touch this seam (R0051 / SEC 014).
/// </para>
/// <para>
/// <b>Parameter rationale.</b> 64 MiB per hash + 4 lanes ≈ 256 MiB peak working
/// set per concurrent login. With 4 iterations this completes in roughly 0.5-1 s
/// on contemporary server hardware — fast enough for an interactive login, slow
/// enough that an attacker with a stolen hash column cannot brute-force at
/// millions of guesses per second. Self-describing PHC format means future parameter
/// rotations (e.g. raising memory to 128 MiB) work transparently for existing
/// hashes — the salt and parameters travel with each row.
/// </para>
/// <para>
/// <b>Constant-time verification.</b> <see cref="Verify"/> uses
/// <see cref="CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte})"/>
/// to compare the re-derived hash against the stored hash, so an attacker cannot
/// distinguish "wrong first byte" from "wrong last byte" by timing the response.
/// On any malformed PHC input — wrong algorithm, garbage segments, missing parts,
/// <c>null</c>, empty — <see cref="Verify"/> returns <c>false</c> WITHOUT throwing
/// so login handlers do not need defensive try/catch.
/// </para>
/// <para>
/// <b>Stateless and thread-safe.</b> Each call constructs its own
/// <see cref="Argon2id"/> instance (the underlying library type is not documented
/// thread-safe) and disposes it before returning. The class itself holds no
/// mutable state, so a single instance registered as <c>Singleton</c> in DI is
/// safe.
/// </para>
/// <para>
/// <b>Logging policy.</b> This class NEVER logs the plaintext, salt, or hash at
/// any level. The PHC string itself contains both salt and hash and is just as
/// sensitive as a database column value; callers must treat it accordingly.
/// </para>
/// </remarks>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    /// <summary>Memory cost in KiB — 64 MiB per OWASP 2024 Argon2id minimum.</summary>
    private const int MemoryCostKib = 65536;

    /// <summary>Time cost — 4 iterations per OWASP 2024 Argon2id default.</summary>
    private const int TimeCost = 4;

    /// <summary>Parallelism — 4 lanes (matches server CPU width without over-committing).</summary>
    private const int Parallelism = 4;

    /// <summary>Salt length in bytes — 16 (128 bits), per OWASP 2024 minimum.</summary>
    private const int SaltLengthBytes = 16;

    /// <summary>Hash output length in bytes — 32 (256 bits), per OWASP 2024 default.</summary>
    private const int HashLengthBytes = 32;

    /// <summary>
    /// Leading PHC segments produced by every <see cref="Hash"/> call. Stored as a
    /// constant so both encoding and decoding share the single source of truth.
    /// </summary>
    private const string PhcPrefix = "$argon2id$v=19$m=65536,t=4,p=4$";

    /// <inheritdoc />
    public string Hash(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            throw new ArgumentException(
                "Password must be non-null and non-whitespace.", nameof(plaintext));
        }

        // Generate a fresh random salt per call so identical passwords across accounts
        // produce different hashes — defeats batch precomputation and rainbow tables.
        var salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);

        var hash = DeriveHash(plaintext, salt);

        // PHC encoding: parameters block then salt then hash, all $-delimited. The
        // parameters are baked into PhcPrefix as constants — when we later rotate
        // (e.g. m=131072), we will change the constants together with the parser to
        // accept the historical block alongside the new one.
        return PhcPrefix
            + Convert.ToBase64String(salt)
            + "$"
            + Convert.ToBase64String(hash);
    }

    /// <inheritdoc />
    public bool Verify(string plaintext, string phcHash)
    {
        // Contract: NEVER throw on bad input. Defensive checks up front let callers
        // (login handlers) treat the result as a pure boolean.
        if (string.IsNullOrEmpty(plaintext) || string.IsNullOrEmpty(phcHash))
        {
            return false;
        }

        if (!TryParsePhc(phcHash, out var salt, out var expectedHash))
        {
            return false;
        }

        byte[] derived;
        try
        {
            derived = DeriveHash(plaintext, salt);
        }
        catch
        {
            // Defensive: any low-level failure in the KDF (OOM, bad parameter range,
            // ...) collapses to "verification failed" rather than propagating.
            return false;
        }

        // FixedTimeEquals compares the full buffer regardless of where the first
        // difference occurs — no early-exit timing leak. The two arrays are always
        // HashLengthBytes long because DeriveHash and TryParsePhc both enforce that
        // size, so the length pre-check is purely defensive.
        if (derived.Length != expectedHash.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(derived, expectedHash);
    }

    /// <summary>
    /// Runs Argon2id with the configured OWASP 2024 parameters over
    /// <paramref name="plaintext"/> (UTF-8 encoded) and <paramref name="salt"/>.
    /// </summary>
    /// <param name="plaintext">Password to hash; must be non-null.</param>
    /// <param name="salt">Salt bytes to combine with the password.</param>
    /// <returns>The 32-byte Argon2id output.</returns>
    private static byte[] DeriveHash(string plaintext, byte[] salt)
    {
        // The Argon2id instance is stateful and not documented thread-safe — construct
        // it per call and dispose immediately. The using statement guarantees the
        // sensitive intermediate buffers (password bytes, internal scratch space) are
        // released even if GetBytes throws.
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(plaintext))
        {
            Salt = salt,
            DegreeOfParallelism = Parallelism,
            MemorySize = MemoryCostKib,
            Iterations = TimeCost,
        };

        return argon2.GetBytes(HashLengthBytes);
    }

    /// <summary>
    /// Parses a PHC-formatted Argon2id string back into its salt and hash byte arrays.
    /// Strict: only the parameter block produced by <see cref="Hash"/> is accepted —
    /// alternative algorithms (argon2i/argon2d) and divergent parameter values are
    /// rejected. This is deliberate: when parameters are rotated, an explicit migration
    /// step must run alongside, so silent acceptance of legacy formats would mask bugs.
    /// </summary>
    /// <param name="phc">Candidate PHC string.</param>
    /// <param name="salt">On success, the decoded 16-byte salt.</param>
    /// <param name="hash">On success, the decoded 32-byte expected hash.</param>
    /// <returns>True when the string parses cleanly and matches the canonical shape; false otherwise.</returns>
    private static bool TryParsePhc(string phc, out byte[] salt, out byte[] hash)
    {
        salt = Array.Empty<byte>();
        hash = Array.Empty<byte>();

        if (!phc.StartsWith(PhcPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Strip the canonical prefix and split the remainder into <saltB64>$<hashB64>.
        var remainder = phc.Substring(PhcPrefix.Length);
        var parts = remainder.Split('$');
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[0]);
            hash = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        // Reject anything that's not the canonical size — this catches PHC strings
        // generated under different parameter sets that happened to share the prefix.
        if (salt.Length != SaltLengthBytes || hash.Length != HashLengthBytes)
        {
            return false;
        }

        return true;
    }
}
