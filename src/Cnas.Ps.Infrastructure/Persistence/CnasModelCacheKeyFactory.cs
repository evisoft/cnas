using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// Custom <see cref="IModelCacheKeyFactory"/> for <see cref="CnasDbContext"/>.
/// The compiled-model cache key includes the concrete
/// <c>IFieldEncryptor</c> identity (CLR type full name) so that two contexts
/// wired with DIFFERENT encryptor implementations do not share a cached model.
/// </summary>
/// <remarks>
/// <para>
/// Without this factory, EF Core caches the compiled model the first time
/// <c>OnModelCreating</c> runs and reuses it for every subsequent context
/// instance constructed against the same <c>DbContextOptions</c> CLR shape.
/// In our case the test suite mixes:
/// </para>
/// <list type="bullet">
///   <item>contexts built with <c>AesFieldEncryptor</c> (production-style) → BankIban / NationalId / Idno / Idnp converters wired</item>
///   <item>contexts built with <c>MissingKeyFieldEncryptor</c> (sentinel, throws on USE) → same model shape, but the converter chain throws on first hit</item>
///   <item>contexts built without an encryptor (test/tooling style) → no converter</item>
/// </list>
/// <para>
/// <b>BUG-003 history.</b> The original implementation discriminated on
/// <c>bool HasFieldEncryptor</c> only. Both <c>AesFieldEncryptor</c> and
/// <c>MissingKeyFieldEncryptor</c> satisfy "has an encryptor", so the two
/// fixtures collided on the same cache key. When the test suite booted the
/// fixtures in parallel, whichever ran <c>OnModelCreating</c> first won the
/// cache and the second fixture inherited the wrong converter wiring —
/// observably failing at <c>SaveChanges</c> the moment any encrypted column
/// was touched. Including the encryptor's CLR type full name in the cache
/// key partitions the cache by encryptor identity and lets the E2E suite
/// run its collections in parallel again.
/// </para>
/// <para>
/// <b>Why the type name and not the instance.</b> A given encryptor type
/// produces the same model regardless of which key bytes / salt it was
/// constructed with — the value converter wiring is keyed on the converter
/// CLR type, not on the underlying secret. Encoding the type name keeps the
/// cache small (one entry per encryptor type) while guaranteeing correctness.
/// </para>
/// </remarks>
public sealed class CnasModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <summary>
    /// Stable identity string used when the context was constructed via the
    /// single-arg ctor (no encryptor). A non-empty literal is intentional so
    /// the discriminator can never collide with a real CLR type name.
    /// </summary>
    private const string NoEncryptorIdentity = "<none>";

    /// <inheritdoc />
    /// <remarks>
    /// Returns a record-based key with structural equality so EF's internal
    /// concurrent dictionary deduplicates compiled models correctly. The key
    /// captures:
    /// <list type="bullet">
    ///   <item><c>context.GetType()</c> — partitions across context CLR types</item>
    ///   <item><paramref name="designTime"/> — keeps design-time and runtime models separate</item>
    ///   <item>encryptor identity — the load-bearing field added for BUG-003</item>
    /// </list>
    /// </remarks>
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);
        var cnas = context as CnasDbContext;
        var encryptorIdentity = cnas?.EncryptorIdentity ?? NoEncryptorIdentity;
        return new CacheKey(context.GetType(), designTime, encryptorIdentity);
    }

    /// <summary>
    /// Composite cache key. Equality is structural (records) so EF's internal
    /// concurrent dictionary correctly deduplicates compiled models.
    /// </summary>
    /// <param name="ContextType">CLR type of the <see cref="DbContext"/> being keyed.</param>
    /// <param name="DesignTime">Whether the model is being built for design-time tooling.</param>
    /// <param name="EncryptorIdentity">
    /// Full CLR type name of the wired <c>IFieldEncryptor</c>, or
    /// <see cref="NoEncryptorIdentity"/> when no encryptor is wired.
    /// Captures the only thing about an encryptor that influences model shape.
    /// </param>
    private sealed record CacheKey(Type ContextType, bool DesignTime, string EncryptorIdentity);
}
