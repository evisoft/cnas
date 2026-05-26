namespace Cnas.Ps.Infrastructure.Persistence;

/// <summary>
/// EF Core mapped database functions for CNAS-specific Postgres extensions. Methods on
/// this class are NEVER called at runtime in C# — they are translated to SQL by EF Core
/// based on the <c>HasDbFunction</c> registration in
/// <see cref="CnasDbContext.OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder)"/>.
/// </summary>
/// <remarks>
/// <para>
/// R0162 / CF 03.13 — diacritic-insensitive search relies on the Postgres
/// <c>unaccent</c> extension. The extension is installed by the
/// <c>EnableUnaccentExtension</c> migration. The mapping wires
/// <see cref="Unaccent(string)"/> to <c>public.unaccent(text)</c> so EF Core query
/// translation produces <c>ILIKE(unaccent(col), unaccent(@p))</c> on the relational
/// path. The InMemory test provider has no <c>unaccent</c>; tests that use InMemory
/// take the <see cref="Cnas.Ps.Application.Search.DiacriticFolding"/> fallback
/// branch instead and never reach this marker.
/// </para>
/// <para>
/// Per CLAUDE.md §2.4: extensions never persist outside the EF pipeline. Calling these
/// methods from C# throws <see cref="InvalidOperationException"/> immediately so any
/// accidental in-process use is loud-fail rather than silently wrong.
/// </para>
/// </remarks>
public static class CnasDbFunctions
{
    /// <summary>
    /// Translates to the Postgres <c>unaccent(text)</c> function (from the
    /// <c>unaccent</c> extension). Folds diacritics off the input. NOT callable from
    /// C# — only EF Core query translation should ever reach this method.
    /// </summary>
    /// <param name="input">The column or constant to fold. Unused at runtime.</param>
    /// <returns>Never returns — the throw is unreachable inside EF query trees.</returns>
    /// <exception cref="InvalidOperationException">
    /// Always — this is a query-only marker. If you see this thrown, you called the
    /// method from C# code instead of inside a LINQ <c>Where</c> / <c>Select</c> tree.
    /// </exception>
    public static string Unaccent(string input) =>
        throw new InvalidOperationException(
            "CnasDbFunctions.Unaccent is a marker for EF Core query translation only. " +
            "Use Cnas.Ps.Application.Search.DiacriticFolding.Fold for in-process folding.");
}
