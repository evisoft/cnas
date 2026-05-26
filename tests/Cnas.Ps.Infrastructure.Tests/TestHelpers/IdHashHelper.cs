using System;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.TestHelpers;

/// <summary>
/// Single source of truth for the deterministic-hash test key used across every test
/// that seeds <see cref="Cnas.Ps.Core.Domain.Solicitant"/> / <see cref="Cnas.Ps.Core.Domain.Contributor"/> /
/// <see cref="Cnas.Ps.Core.Domain.InsuredPerson"/> / <see cref="Cnas.Ps.Core.Domain.UserProfile"/>
/// records and needs to populate the <c>*Hash</c> shadow column.
/// </summary>
/// <remarks>
/// <para>
/// The shadow columns back UNIQUE INDEXes and equality lookups against the encrypted
/// plaintext columns (TOR SEC 035 follow-up batch). In production
/// <see cref="Cnas.Ps.Application.Abstractions.IDeterministicHasher"/> is salted with a
/// secret bound from the secrets manager; in tests we use a single in-process
/// <see cref="Hmac256Hasher"/> instance built from a fixed 32-byte key.
/// </para>
/// <para>
/// <b>Why a static helper.</b> Most integration tests build their entity rows
/// in-line (no service injection) using object initialisers — they have no service
/// graph available to resolve <see cref="IDeterministicHasher"/>. A shared static
/// helper lets every test populate the <c>*Hash</c> column from the canonical
/// plaintext through a single canonicalization pipeline, so test seedings stay
/// consistent with production canonicalization semantics (Trim + ToUpperInvariant).
/// </para>
/// <para>
/// <b>Test isolation.</b> The static key here is deliberately distinct from the
/// production key. There is no risk of cross-contamination because the production
/// salt lives in the secrets manager and is never checked in. Tests that need to
/// observe key-sensitive behaviour build their own <see cref="Hmac256Hasher"/> with
/// a separate key — see <see cref="Cnas.Ps.Infrastructure.Tests.Security.Hmac256HasherTests"/>.
/// </para>
/// </remarks>
internal static class IdHashHelper
{
    /// <summary>
    /// 32-byte (256-bit) test salt — fixed and committed so test runs are reproducible.
    /// Distinct from any production deployment salt.
    /// </summary>
    private static readonly byte[] TestSalt =
    [
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
        0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7,
        0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
    ];

    /// <summary>
    /// Singleton <see cref="IDeterministicHasher"/> instance bound to <see cref="TestSalt"/>.
    /// Tests resolve it through this property when they need the SUT to share the
    /// same canonicalization with the test seed code.
    /// </summary>
    public static IDeterministicHasher Instance { get; } = BuildHasher();

    /// <summary>
    /// Convenience: canonicalize <paramref name="plaintext"/> via the test hasher and
    /// return the base64 hash. Used directly inside object initialisers to populate
    /// <c>NationalIdHash</c> / <c>IdnoHash</c> / <c>IdnpHash</c> alongside the plaintext.
    /// </summary>
    /// <param name="plaintext">The national-identifier plaintext (raw — the hasher canonicalizes).</param>
    /// <returns>44-character base64 HMAC-SHA256 hash matching the column declaration.</returns>
    public static string Hash(string plaintext) => Instance.ComputeHash(plaintext);

    /// <summary>Builds the test hasher once at type initialisation.</summary>
    private static Hmac256Hasher BuildHasher()
    {
        var opts = new FieldHashingOptions { SaltKey = Convert.ToBase64String(TestSalt) };
        return new Hmac256Hasher(Options.Create(opts));
    }
}
