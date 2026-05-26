using System;
using Cnas.Ps.Application.Abstractions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cnas.Ps.Infrastructure.Persistence.Conversion;

/// <summary>
/// EF Core value converter that transparently encrypts a <see cref="string"/>
/// domain property to a versioned ciphertext envelope at the column layer,
/// and decrypts it back when materializing the entity. Per CLAUDE.md §5.7,
/// used for highly-confidential fields (bank IBAN, government IDs, ...) that
/// must not be readable from a database backup or via SQL injection.
/// </summary>
/// <remarks>
/// <para>
/// The converter is constructed with an injected <see cref="IFieldEncryptor"/>
/// rather than registered via the default <see cref="ValueConverter{TModel,TProvider}"/>
/// constructor chain because EF Core does not perform DI on value converters
/// by default. The owning <see cref="Cnas.Ps.Infrastructure.Persistence.CnasDbContext"/>
/// receives the encryptor through its constructor and wires this converter
/// in <c>OnModelCreating</c>.
/// </para>
/// <para>
/// This converter targets NON-NULL strings. EF Core handles the NULL path
/// automatically when the entity property is declared as <c>string?</c> —
/// the converter is invoked only for non-null values, so a null on the
/// domain side becomes a NULL column at rest (no sentinel ciphertext is
/// written, which would otherwise leak a "has IBAN" / "no IBAN" bit).
/// </para>
/// <para>
/// Equality lookups against the encrypted column will NOT work: each
/// encryption samples a fresh nonce, so the same plaintext encrypts to a
/// different ciphertext every time. Apply this converter only to columns
/// that the system reads by primary key or by a separate non-encrypted
/// index.
/// </para>
/// </remarks>
public sealed class EncryptedStringConverter : ValueConverter<string, string>
{
    /// <summary>
    /// Initializes a converter that delegates to <paramref name="encryptor"/>
    /// for both directions. The same converter instance is reused across all
    /// property mappings on a given <see cref="Cnas.Ps.Infrastructure.Persistence.CnasDbContext"/>;
    /// it is stateless and therefore thread-safe.
    /// </summary>
    /// <param name="encryptor">
    /// Field encryptor resolved from DI by the owning DbContext. Must not be
    /// <c>null</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="encryptor"/> is <c>null</c>.</exception>
    public EncryptedStringConverter(IFieldEncryptor encryptor)
        : base(
            // Domain → Provider: encrypt on write.
            domainValue => encryptor.Encrypt(domainValue),
            // Provider → Domain: decrypt on read. The cast on the inner
            // closure binds the encryptor reference for the lifetime of the
            // converter (and therefore of the DbContext model cache).
            providerValue => encryptor.Decrypt(providerValue))
    {
        ArgumentNullException.ThrowIfNull(encryptor);
    }
}
