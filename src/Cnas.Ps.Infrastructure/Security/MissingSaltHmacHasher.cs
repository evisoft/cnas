using System;
using Cnas.Ps.Application.Abstractions;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// Sentinel <see cref="IDeterministicHasher"/> registered when the deployment
/// did not supply a salt for <see cref="Hmac256Hasher"/>. Every call to
/// <see cref="ComputeHash"/> throws <see cref="InvalidOperationException"/>
/// so the failure is loud at the point of impact (the first attempt to
/// compute or compare a national-id shadow column) rather than silently
/// writing an unsalted SHA-256 to the database.
/// </summary>
/// <remarks>
/// <para>
/// The hash columns back UNIQUE INDEXes and equality joins (Annex 6f
/// Solicitant→InsuredPerson, Solicitant uniqueness on NationalId, …). A
/// missing salt would otherwise no-op the brute-force resistance the primitive
/// exists to provide. Per CLAUDE.md §5.7 a missing key MUST fail loud rather
/// than silently degrade — registering this sentinel lets the application
/// boot (unrelated health checks still pass) while guaranteeing that any
/// touch of a hash column trips an immediate exception that surfaces in the
/// alert pipeline.
/// </para>
/// <para>
/// Tests and local-dev environments that legitimately do not exercise
/// hash-driven equality lookups are unaffected; they only meet the sentinel
/// if a code path actually calls <see cref="ComputeHash"/>.
/// </para>
/// </remarks>
internal sealed class MissingSaltHmacHasher : IDeterministicHasher
{
    /// <summary>Diagnostic message used by the throw — kept consistent for log greps.</summary>
    private const string Message =
        "Field hashing is not configured (Cnas:FieldHashing:SaltKey is missing). " +
        "Bind the base64-encoded HMAC-SHA256 salt (≥ 32 bytes) from the secrets manager " +
        "(Vault / k8s Secret / MCloud KMS) before computing or comparing any national-id shadow column.";

    /// <inheritdoc />
    public string ComputeHash(string canonicalValue) => throw new InvalidOperationException(Message);
}
