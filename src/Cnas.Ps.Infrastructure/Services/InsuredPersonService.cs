using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Concrete implementation of <see cref="IInsuredPersonService"/> backed by EF Core.
/// Owns the Annex 2 <c>Persoane asigurate</c> registry — registration, lookup,
/// search, deceased-flag recording, and soft-delete. All external IDs are Sqid-encoded
/// (CLAUDE.md RULE 3); all timestamps go through <see cref="ICnasTimeProvider"/>;
/// all sensitive mutations are journaled via <see cref="IAuditService"/>.
/// </summary>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping.</param>
/// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller information for audit attribution.</param>
/// <param name="audit">Audit journal façade.</param>
/// <param name="idHasher">
/// Deterministic HMAC hasher for the <see cref="InsuredPerson.IdnpHash"/> shadow column.
/// Equality lookups (<c>WHERE Idnp == X</c>) go through this — the encrypted plaintext
/// column cannot be queried directly. See <see cref="IDeterministicHasher"/>.
/// </param>
public sealed class InsuredPersonService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    IDeterministicHasher idHasher) : IInsuredPersonService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly IDeterministicHasher _idHasher = idHasher;

    /// <summary>Audit event code emitted when a new insured person is registered.</summary>
    private const string EvtRegistered = "INSURED_PERSON.REGISTERED";

    /// <summary>Audit event code emitted when an insured person is flagged deceased.</summary>
    private const string EvtDeceasedRecorded = "INSURED_PERSON.DECEASED_RECORDED";

    /// <summary>Audit event code emitted when an insured person is soft-deleted.</summary>
    private const string EvtDeactivated = "INSURED_PERSON.DEACTIVATED";

    /// <summary>
    /// Cached JSON serializer options — reused across audit-payload builders to satisfy
    /// the CA1869 analyzer guidance (avoid per-call options construction).
    /// </summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task<Result<string>> RegisterAsync(InsuredPersonRegistrationInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Validate the IDNP at the service boundary via the value object so the same
        //    InvalidIdnp error code surfaces whether the call came through the validator
        //    (REST) or another internal pathway.
        var idnpResult = Idnp.TryCreate(input.Idnp);
        if (idnpResult.IsFailure)
        {
            return Result<string>.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!);
        }
        var idnp = idnpResult.Value;

        // 2. Reject duplicates: an *active* insured person with the same IDNP must not exist.
        //    Soft-deleted (IsActive=false) records are permitted to be re-registered.
        //    Equality lookup goes through the IdnpHash shadow column because the plaintext
        //    Idnp column is encrypted at rest. See InsuredPerson.Idnp / IdnpHash XML doc.
        var idnpHash = _idHasher.ComputeHash(idnp.Value);
        var exists = await _db.InsuredPersons
            .AnyAsync(p => p.IdnpHash == idnpHash && p.IsActive, ct)
            .ConfigureAwait(false);
        if (exists)
        {
            return Result<string>.Failure(ErrorCodes.Conflict, "Insured person already registered.");
        }

        // 3. Persist a fresh aggregate with audit metadata populated from caller/clock.
        //    IdnpHash mirrors Idnp via the deterministic hasher — the contract on
        //    InsuredPerson.IdnpHash requires every Idnp write to set the hash.
        var now = _clock.UtcNow;
        var entity = new InsuredPerson
        {
            Idnp = idnp.Value,
            IdnpHash = idnpHash,
            LastName = input.LastName,
            FirstName = input.FirstName,
            Patronymic = input.Patronymic,
            BirthDate = input.BirthDate,
            IsDeceased = false,
            RegisteredAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.InsuredPersons.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0502 / SEC-AUDIT — record the IDNP HASH on the audit row, never the
        // raw IDNP. The audit row is queryable by operators with broad access,
        // and persisting the cleartext identifier here would defeat the
        // encrypted-at-rest contract on InsuredPerson.Idnp. The deterministic
        // hash is sufficient for forensics (same input → same hash → same
        // audit chain) without leaking the underlying national identifier.
        await _audit.RecordAsync(
            EvtRegistered,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(InsuredPerson),
            entity.Id,
            JsonSerializer.Serialize(new { idnpHash = entity.IdnpHash }, CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result<string>.Success(_sqids.Encode(entity.Id));
    }

    /// <inheritdoc />
    public async Task<Result<InsuredPersonOutput>> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return Result<InsuredPersonOutput>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var entity = await _db.InsuredPersons
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<InsuredPersonOutput>.Failure(ErrorCodes.NotFound, "Insured person not found.");
        }

        return Result<InsuredPersonOutput>.Success(ToOutput(entity));
    }

    /// <inheritdoc />
    public async Task<Result<InsuredPersonOutput>> GetByIdnpAsync(string idnp, CancellationToken ct = default)
    {
        var idnpResult = Idnp.TryCreate(idnp);
        if (idnpResult.IsFailure)
        {
            return Result<InsuredPersonOutput>.Failure(idnpResult.ErrorCode!, idnpResult.ErrorMessage!);
        }

        var canonical = idnpResult.Value.Value;
        // Equality lookup via the hash shadow column — the plaintext Idnp column is
        // encrypted and cannot be queried directly. See InsuredPerson.IdnpHash XML doc.
        var canonicalHash = _idHasher.ComputeHash(canonical);
        var entity = await _db.InsuredPersons
            .SingleOrDefaultAsync(p => p.IdnpHash == canonicalHash && p.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<InsuredPersonOutput>.Failure(ErrorCodes.NotFound, "Insured person not found.");
        }

        return Result<InsuredPersonOutput>.Success(ToOutput(entity));
    }

    /// <summary>
    /// Searches the active-insured-person registry by IDNP or by a substring of the
    /// <see cref="InsuredPerson.LastName"/> / <see cref="InsuredPerson.FirstName"/> /
    /// <see cref="InsuredPerson.Patronymic"/>. Returns a single page of results ordered
    /// by <c>LastName</c> then <c>FirstName</c> ascending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Encryption-driven design.</b> The <see cref="InsuredPerson.Idnp"/> column is
    /// encrypted at rest by <see cref="Cnas.Ps.Infrastructure.Persistence.Conversion.EncryptedStringConverter"/>
    /// (TOR SEC 035 / CLAUDE.md §5.7) — different ciphertext per row, so neither
    /// <c>ILIKE '%X%'</c> nor <c>EF.Functions.Like(...)</c> can return a match for any
    /// IDNP substring. The search therefore branches on the SHAPE of the query string:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Full 13-digit numeric query</b> (e.g. <c>"2000000000007"</c>): canonicalize
    ///     and hash via <see cref="IDeterministicHasher.ComputeHash"/>, then equality-match
    ///     against the <see cref="InsuredPerson.IdnpHash"/> shadow column. OR-combined with
    ///     the name-field substring search so a 13-digit string that happens to occur in a
    ///     name still surfaces. The hash branch translates universally (it's a plain <c>==</c>
    ///     on a base64 string) — no provider seam needed.
    ///   </item>
    ///   <item>
    ///     <b>Anything else</b> (partial digits, alphanumeric, names): substring-match the
    ///     unencrypted name fields only. Partial-IDNP lookup is <b>intentionally unsupported</b>:
    ///     it would require a blind-index / n-gram-hash scheme, which is a separate design
    ///     batch. Users who want to find an insured person by IDNP MUST type all 13 digits.
    ///   </item>
    /// </list>
    /// <para>
    /// <b>Worked example.</b> Given two insured persons
    /// <c>{Idnp="2000000000007", LastName="Popescu"}</c> and
    /// <c>{Idnp="1003600012346", LastName="Ionescu"}</c>:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>Search("2000000000007")</c> → hash branch → returns Popescu.</item>
    ///   <item><c>Search("20000")</c> → name branch only → no name contains it → empty.</item>
    ///   <item><c>Search("Popescu")</c> → name branch only → returns Popescu.</item>
    ///   <item><c>Search("  2000000000007  ")</c> → hash branch (Trim+ToUpper canonicalized by the hasher) → Popescu.</item>
    /// </list>
    /// </remarks>
    /// <param name="nameOrIdnp">
    /// Free-form query. <c>null</c> / empty / whitespace returns all active insured persons.
    /// </param>
    /// <param name="page">Page number (1-based) and size; clamped to 1..200 for size.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paged result ordered by <c>LastName</c> then <c>FirstName</c>.</returns>
    public async Task<Result<PagedResult<InsuredPersonListItem>>> SearchAsync(
        string? nameOrIdnp,
        PageRequest page,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var pageSize = Math.Clamp(page.PageSize, 1, 200);
        var pageNumber = Math.Max(1, page.Page);
        var skip = (pageNumber - 1) * pageSize;

        IQueryable<InsuredPerson> query = _db.InsuredPersons.Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(nameOrIdnp))
        {
            var trimmed = nameOrIdnp.Trim();

            if (IsLikelyNationalIdentifier(trimmed))
            {
                // Full 13-digit identifier: equality match against the IdnpHash shadow column.
                // The plaintext Idnp column is encrypted (different ciphertext per row), so a
                // SQL/EF substring operator cannot resolve it. The hash is canonicalized by
                // IDeterministicHasher (Trim + ToUpperInvariant), so we can hand the raw
                // trimmed input straight in — no double-canonicalization at the call site.
                // OR-combined with the unencrypted name-field substrings so a 13-digit string
                // that happens to appear in a name still surfaces. The == predicate translates
                // on every EF provider — no in-memory vs Npgsql seam needed for this branch.
                var idnpHash = _idHasher.ComputeHash(trimmed);
                // R0162 / CF 03.13 — diacritic-insensitive search. Relational path uses
                // unaccent(col) ILIKE unaccent(pattern); InMemory path folds both sides via
                // DiacriticFolding.Fold. IDNP is ASCII-only so the hash branch needs no folding.
                // R0164 / UI 012 / CF 03.02 — wildcard-mask translation. Fold first (R0162)
                // then run through WildcardMask which translates '*' → '%' (LIKE) / '.*'
                // (regex) and escapes any literal LIKE wildcards typed by the user.
                var folded = DiacriticFolding.Fold(trimmed);
                var likeFolded = WildcardMask.ToLikePattern(folded);
                if (IsRelationalProvider(_db))
                {
                    query = query.Where(p =>
                        p.IdnpHash == idnpHash ||
                        EF.Functions.ILike(CnasDbFunctions.Unaccent(p.LastName), likeFolded) ||
                        EF.Functions.ILike(CnasDbFunctions.Unaccent(p.FirstName), likeFolded) ||
                        (p.Patronymic != null && EF.Functions.ILike(CnasDbFunctions.Unaccent(p.Patronymic), likeFolded)));
                }
                else
                {
                    // R0162 InMemory fallback — DiacriticFolding.Fold is a static method
                    // the InMemory provider can invoke client-side via its LINQ-to-Objects
                    // translator. Keeping the .Where on IQueryable preserves the EF async
                    // provider so subsequent LongCountAsync / ToListAsync still translate.
                    // R0164 — substring Contains is replaced with WildcardMask.ToRegex so
                    // the InMemory branch honours the same mask semantics as the relational
                    // path (anchored when '*' is present, unanchored substring otherwise).
                    var regex = WildcardMask.ToRegex(folded);
                    query = query.Where(p =>
                        p.IdnpHash == idnpHash ||
                        regex.IsMatch(DiacriticFolding.Fold(p.LastName)) ||
                        regex.IsMatch(DiacriticFolding.Fold(p.FirstName)) ||
                        (p.Patronymic != null && regex.IsMatch(DiacriticFolding.Fold(p.Patronymic))));
                }
            }
            else
            {
                // Not a full identifier: substring search on the unencrypted name fields only.
                // Partial-IDNP search is intentionally unsupported — the encrypted column
                // cannot answer LIKE queries. The provider seam wraps ONLY this branch
                // because EF.Functions.ILike is Postgres-specific.
                // R0162 / CF 03.13 — diacritic-insensitive (see above for the rationale).
                // R0164 / UI 012 / CF 03.02 — wildcard mask (see above for the rationale).
                var folded = DiacriticFolding.Fold(trimmed);
                var likeFolded = WildcardMask.ToLikePattern(folded);
                if (IsRelationalProvider(_db))
                {
                    query = query.Where(p =>
                        EF.Functions.ILike(CnasDbFunctions.Unaccent(p.LastName), likeFolded) ||
                        EF.Functions.ILike(CnasDbFunctions.Unaccent(p.FirstName), likeFolded) ||
                        (p.Patronymic != null && EF.Functions.ILike(CnasDbFunctions.Unaccent(p.Patronymic), likeFolded)));
                }
                else
                {
                    // R0162 + R0164 InMemory fallback — see paired branch above for rationale.
                    var regex = WildcardMask.ToRegex(folded);
                    query = query.Where(p =>
                        regex.IsMatch(DiacriticFolding.Fold(p.LastName)) ||
                        regex.IsMatch(DiacriticFolding.Fold(p.FirstName)) ||
                        (p.Patronymic != null && regex.IsMatch(DiacriticFolding.Fold(p.Patronymic))));
                }
            }
        }

        query = query.OrderBy(p => p.LastName).ThenBy(p => p.FirstName);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await query
            .Skip(skip).Take(pageSize)
            .Select(p => new InsuredPersonListItem(
                _sqids.Encode(p.Id),
                p.Idnp,
                BuildFullName(p.LastName, p.FirstName, p.Patronymic),
                p.IsDeceased))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Result<PagedResult<InsuredPersonListItem>>.Success(
            new PagedResult<InsuredPersonListItem>(rows, pageNumber, pageSize, total));
    }

    /// <inheritdoc />
    public async Task<Result> MarkDeceasedAsync(string id, DateOnly dateOfDeath, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var entity = await _db.InsuredPersons
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Insured person not found.");
        }

        var now = _clock.UtcNow;
        entity.IsDeceased = true;
        entity.DateOfDeath = dateOfDeath;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0502 / SEC-AUDIT — record the IDNP HASH on the audit row, never the raw IDNP.
        await _audit.RecordAsync(
            EvtDeceasedRecorded,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(InsuredPerson),
            entity.Id,
            JsonSerializer.Serialize(
                new { idnpHash = entity.IdnpHash, dateOfDeath = dateOfDeath.ToString("yyyy-MM-dd") },
                CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> DeactivateAsync(string id, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var entity = await _db.InsuredPersons
            .SingleOrDefaultAsync(p => p.Id == decoded.Value && p.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Insured person not found.");
        }

        var now = _clock.UtcNow;
        entity.IsActive = false;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0502 / SEC-AUDIT — record the IDNP HASH on the audit row, never the raw IDNP.
        await _audit.RecordAsync(
            EvtDeactivated,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(InsuredPerson),
            entity.Id,
            JsonSerializer.Serialize(new { idnpHash = entity.IdnpHash }, CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>Maps an <see cref="InsuredPerson"/> entity to its outbound DTO with Sqid id.</summary>
    /// <param name="entity">Loaded entity (must not be null).</param>
    /// <returns>Sqid-encoded output projection.</returns>
    private InsuredPersonOutput ToOutput(InsuredPerson entity) => new(
        _sqids.Encode(entity.Id),
        entity.Idnp,
        entity.LastName,
        entity.FirstName,
        entity.Patronymic,
        entity.BirthDate,
        entity.IsDeceased,
        entity.DateOfDeath,
        entity.RegisteredAtUtc,
        entity.LastRspSyncUtc);

    /// <summary>
    /// Composes the display name for list-view rows: <c>"Last First [Patronymic]"</c> with
    /// the patronymic appended only when present.
    /// </summary>
    /// <param name="lastName">Family name.</param>
    /// <param name="firstName">Given name.</param>
    /// <param name="patronymic">Optional patronymic.</param>
    /// <returns>Single-line display name without trailing whitespace.</returns>
    private static string BuildFullName(string lastName, string firstName, string? patronymic) =>
        string.IsNullOrWhiteSpace(patronymic)
            ? $"{lastName} {firstName}"
            : $"{lastName} {firstName} {patronymic}";

    /// <summary>
    /// Detects whether the underlying <see cref="ICnasDbContext"/> is backed by a relational
    /// provider (Npgsql in production) vs the in-memory test fake. This is the single seam
    /// that lets the search query stay native PostgreSQL ILIKE in production while remaining
    /// executable against EF Core InMemory in integration tests.
    /// </summary>
    /// <remarks>
    /// The seam wraps ONLY the name-field substring branches of <see cref="SearchAsync"/>;
    /// the IDNP-hash equality branch uses a universal <c>==</c> predicate that translates
    /// on every EF Core provider, so it intentionally does not consult this method.
    /// </remarks>
    /// <param name="db">The application's DB context abstraction.</param>
    /// <returns>True for Postgres / SQL Server / SQLite; false for InMemory or other in-process providers.</returns>
    private static bool IsRelationalProvider(ICnasDbContext db)
    {
        if (db is not DbContext concrete)
        {
            return false;
        }
        var providerName = concrete.Database.ProviderName ?? string.Empty;
        return !providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shape predicate: returns <c>true</c> when <paramref name="trimmedQuery"/> looks
    /// like a complete Moldovan national identifier — exactly 13 characters, all digits.
    /// Used by <see cref="SearchAsync"/> to decide whether to take the IDNP-hash equality
    /// branch or fall back to the name-field substring branch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a SHAPE detection only — it does NOT validate the mod-10 checksum.
    /// We deliberately do not invoke <see cref="Cnas.Ps.Core.ValueObjects.Idnp.TryCreate"/>:
    /// a search query is a user-typed string, not a registration input, and an invalid
    /// checksum should return "no results" rather than "invalid IDNP" (the user would
    /// have no way to know whether they mistyped a digit or whether the row doesn't exist).
    /// The hash branch will simply find zero rows for a typo'd 13-digit string — that is
    /// the correct user-visible behaviour.
    /// </para>
    /// <para>
    /// Inputs with non-digit characters (e.g. <c>"20000abc"</c>, <c>"Popescu"</c>) MUST
    /// return false here so they are routed to the name-substring branch — extracting and
    /// hashing only the digits would re-introduce the partial-IDNP bug we are intentionally
    /// NOT supporting in this batch.
    /// </para>
    /// </remarks>
    /// <param name="trimmedQuery">The already-trimmed query string.</param>
    /// <returns><c>true</c> when the input is exactly 13 digits; <c>false</c> otherwise.</returns>
    private static bool IsLikelyNationalIdentifier(string trimmedQuery)
    {
        if (trimmedQuery.Length != 13)
        {
            return false;
        }
        // Manual digit check avoids LINQ allocation in the hot search path.
        for (int i = 0; i < trimmedQuery.Length; i++)
        {
            if (!char.IsDigit(trimmedQuery[i]))
            {
                return false;
            }
        }
        return true;
    }
}
