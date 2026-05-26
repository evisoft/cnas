using System;
using Cnas.Ps.Application.Abstractions;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// Sentinel <see cref="IFieldEncryptor"/> registered when the deployment did
/// not supply a master key. Every method throws <see cref="InvalidOperationException"/>
/// on first use so the failure is loud at the point of impact (the first
/// attempt to write or read an encrypted column) rather than silently
/// leaking plaintext to the database.
/// </summary>
/// <remarks>
/// <para>
/// Field encryption is a security boundary. Per CLAUDE.md §5.7, a missing key
/// MUST fail rather than no-op: the alternative (silently writing plaintext)
/// would defeat the entire control because nobody would notice during a
/// staging dry-run. Registering this sentinel lets the application boot
/// (so unrelated health checks still pass) while guaranteeing that any
/// touch of an encrypted column trips an immediate exception that surfaces
/// in the alert pipeline.
/// </para>
/// <para>
/// Tests and local-dev environments that legitimately do not exercise
/// encrypted columns are unaffected; they only meet the sentinel if a code
/// path actually touches an encrypted field.
/// </para>
/// </remarks>
internal sealed class MissingKeyFieldEncryptor : IFieldEncryptor
{
    /// <summary>Diagnostic message used by every throw — kept consistent for log greps.</summary>
    private const string Message =
        "Field encryption is not configured (Cnas:FieldEncryption:Key is missing). " +
        "Bind the base64-encoded 256-bit AES master key from the secrets manager " +
        "(Vault / k8s Secret / MCloud KMS) before reading or writing any encrypted column.";

    /// <inheritdoc />
    public string Encrypt(string plaintext) => throw new InvalidOperationException(Message);

    /// <inheritdoc />
    public string Decrypt(string ciphertext) => throw new InvalidOperationException(Message);
}
