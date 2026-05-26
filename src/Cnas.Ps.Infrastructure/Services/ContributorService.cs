using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Search;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Core.ValueObjects;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Concrete implementation of <see cref="IContributorService"/> backed by EF Core.
/// Owns the Annex 1 <c>Plătitori de contribuții</c> registry — registration, lookup,
/// search, insolvency toggling, and soft-delete. All external IDs are Sqid-encoded
/// (CLAUDE.md RULE 3); all timestamps go through <see cref="ICnasTimeProvider"/>;
/// all sensitive mutations are journaled via <see cref="IAuditService"/>.
/// </summary>
/// <param name="db">EF Core context abstraction.</param>
/// <param name="sqids">Sqid encoder/decoder for external id round-tripping.</param>
/// <param name="clock">UTC clock — never call <see cref="DateTime.UtcNow"/> directly.</param>
/// <param name="caller">Authenticated caller information for audit attribution.</param>
/// <param name="audit">Audit journal façade.</param>
/// <param name="idHasher">
/// Deterministic HMAC hasher for the <see cref="Contributor.IdnoHash"/> shadow column.
/// Equality lookups (<c>WHERE Idno == X</c>) go through this — the encrypted plaintext
/// column cannot be queried directly. See <see cref="IDeterministicHasher"/>.
/// </param>
public sealed class ContributorService(
    ICnasDbContext db,
    ISqidService sqids,
    ICnasTimeProvider clock,
    ICallerContext caller,
    IAuditService audit,
    IDeterministicHasher idHasher) : IContributorService
{
    private readonly ICnasDbContext _db = db;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly ICallerContext _caller = caller;
    private readonly IAuditService _audit = audit;
    private readonly IDeterministicHasher _idHasher = idHasher;

    /// <summary>Audit event code emitted when a new contributor is registered.</summary>
    private const string EvtRegistered = "CONTRIBUTOR.REGISTERED";

    /// <summary>Audit event code emitted when a contributor is flagged insolvent.</summary>
    private const string EvtInsolventSet = "CONTRIBUTOR.INSOLVENT_SET";

    /// <summary>Audit event code emitted when a contributor's insolvent flag is cleared.</summary>
    private const string EvtSolventRestored = "CONTRIBUTOR.SOLVENT_RESTORED";

    /// <summary>Audit event code emitted when a contributor is soft-deleted (de-registered).</summary>
    private const string EvtDeactivated = "CONTRIBUTOR.DEACTIVATED";

    // ─── R0305 / TOR Annex 1 — Business-Process audit event codes ───

    /// <summary>BP 1.2 — primary attributes updated.</summary>
    private const string EvtUpdated = "CONTRIBUTOR.UPDATED";

    /// <summary>BP 1.3 — administratively deactivated (distinct from the soft-delete deregistration above).</summary>
    private const string EvtDeactivatedBp = "CONTRIBUTOR.DEACTIVATED_BP";

    /// <summary>BP 1.4 — reactivated.</summary>
    private const string EvtReactivated = "CONTRIBUTOR.REACTIVATED";

    /// <summary>BP 1.5 — merged into a survivor row.</summary>
    private const string EvtMerged = "CONTRIBUTOR.MERGED";

    /// <summary>BP 1.7 — admin-recorded field-level correction (audit-only, no field write).</summary>
    private const string EvtAdminCorrection = "CONTRIBUTOR.ADMIN_CORRECTION";

    /// <summary>BP 1.9 — terminal: deceased (natural person) or dissolved (legal person).</summary>
    private const string EvtDeceasedOrDissolved = "CONTRIBUTOR.DECEASED_OR_DISSOLVED";

    /// <summary>RBAC permission code required for BP 1.7 admin-correction.</summary>
    private const string PermAdminCorrect = "Contributor.AdminCorrect";

    /// <summary>
    /// Cached JSON serializer options — case-insensitive on deserialise, default on
    /// serialise. Reused across audit-payload builders to satisfy the CA1869 analyzer
    /// guidance (avoid per-call options construction).
    /// </summary>
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task<Result<string>> RegisterAsync(ContributorRegistrationInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Validate the IDNO at the service boundary via the value object — propagating
        //    the same InvalidIdno error code the validator would emit so callers see a
        //    consistent contract regardless of which layer caught the bad input.
        var idnoResult = Idno.TryCreate(input.Idno);
        if (idnoResult.IsFailure)
        {
            return Result<string>.Failure(idnoResult.ErrorCode!, idnoResult.ErrorMessage!);
        }
        var idno = idnoResult.Value;

        // 2. Reject duplicates: an *active* contributor with the same IDNO must not exist.
        //    Soft-deleted (IsActive=false) records are allowed to be re-registered.
        //    Equality lookup goes through the IdnoHash shadow column because the plaintext
        //    Idno column is encrypted at rest (different ciphertext per row → no equality
        //    match in SQL). See Contributor.Idno / IdnoHash XML doc for the contract.
        var idnoHash = _idHasher.ComputeHash(idno.Value);
        var exists = await _db.Contributors
            .AnyAsync(c => c.IdnoHash == idnoHash && c.IsActive, ct)
            .ConfigureAwait(false);
        if (exists)
        {
            return Result<string>.Failure(ErrorCodes.Conflict, "Contributor already registered.");
        }

        // 3. Persist a fresh aggregate with audit metadata populated from caller/clock.
        //    IdnoHash mirrors Idno via the deterministic hasher — the contract on
        //    Contributor.IdnoHash requires every Idno write to set the hash.
        var now = _clock.UtcNow;
        var entity = new Contributor
        {
            Idno = idno.Value,
            IdnoHash = idnoHash,
            Denumire = input.Denumire,
            CfojCode = input.CfojCode,
            CaemCode = input.CaemCode,
            IsInsolvent = false,
            RegisteredAtUtc = now,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.Contributors.Add(entity);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0502 / SEC-AUDIT — record the IDNO HASH on the audit row, never the
        // raw IDNO. The audit row is queryable by operators with broad access,
        // and persisting the cleartext identifier here would defeat the
        // encrypted-at-rest contract on Contributor.Idno. The deterministic
        // hash is sufficient for forensics (same input → same hash → same
        // audit chain) without leaking the underlying national identifier.
        await _audit.RecordAsync(
            EvtRegistered,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Contributor),
            entity.Id,
            JsonSerializer.Serialize(new { idnoHash = entity.IdnoHash }, CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        // R0305 — per-BP counter so operators can chart Annex-1 BP invocation volumes.
        CnasMeter.ContributorBpInvoked.Add(1, new KeyValuePair<string, object?>("bp", "Register"));

        return Result<string>.Success(_sqids.Encode(entity.Id));
    }

    /// <inheritdoc />
    public async Task<Result<ContributorOutput>> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return Result<ContributorOutput>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var entity = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == decoded.Value && c.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<ContributorOutput>.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        return Result<ContributorOutput>.Success(ToOutput(entity));
    }

    /// <inheritdoc />
    public async Task<Result<ContributorOutput>> GetByIdnoAsync(string idno, CancellationToken ct = default)
    {
        var idnoResult = Idno.TryCreate(idno);
        if (idnoResult.IsFailure)
        {
            return Result<ContributorOutput>.Failure(idnoResult.ErrorCode!, idnoResult.ErrorMessage!);
        }

        var canonical = idnoResult.Value.Value;
        // Equality lookup via the hash shadow column — the plaintext Idno column is encrypted
        // and cannot be queried directly. See Contributor.IdnoHash XML doc.
        var canonicalHash = _idHasher.ComputeHash(canonical);
        var entity = await _db.Contributors
            .SingleOrDefaultAsync(c => c.IdnoHash == canonicalHash && c.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<ContributorOutput>.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        return Result<ContributorOutput>.Success(ToOutput(entity));
    }

    /// <summary>
    /// Searches the active-contributor registry by IDNO or by a substring of the
    /// <see cref="Contributor.Denumire"/> (legal name). Returns a single page of results
    /// ordered by <c>Denumire</c> ascending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Encryption-driven design.</b> The <see cref="Contributor.Idno"/> column is
    /// encrypted at rest by <see cref="Cnas.Ps.Infrastructure.Persistence.Conversion.EncryptedStringConverter"/>
    /// (TOR SEC 035 / CLAUDE.md §5.7) — different ciphertext per row, so neither
    /// <c>ILIKE '%X%'</c> nor <c>EF.Functions.Like(...)</c> can return a match for any
    /// IDNO substring. The search therefore branches on the SHAPE of the query string:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Full 13-digit numeric query</b> (e.g. <c>"2000000000007"</c>): canonicalize
    ///     and hash via <see cref="IDeterministicHasher.ComputeHash"/>, then equality-match
    ///     against the <see cref="Contributor.IdnoHash"/> shadow column. OR-combined with
    ///     the name-field substring search so a 13-digit string that happens to occur in a
    ///     <c>Denumire</c> still surfaces. The hash branch translates universally (it's a
    ///     plain <c>==</c> on a base64 string) — no provider seam needed.
    ///   </item>
    ///   <item>
    ///     <b>Anything else</b> (partial digits, alphanumeric, names): substring-match the
    ///     unencrypted <see cref="Contributor.Denumire"/> column only. Partial-IDNO lookup
    ///     is <b>intentionally unsupported</b>: it would require a blind-index / n-gram-hash
    ///     scheme, which is a separate design batch. Users who want to find a contributor
    ///     by IDNO MUST type all 13 digits.
    ///   </item>
    /// </list>
    /// <para>
    /// <b>Worked example.</b> Given two contributors
    /// <c>{Idno="2000000000007", Denumire="Acme SRL"}</c> and
    /// <c>{Idno="1003600012346", Denumire="Beta SRL"}</c>:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>Search("2000000000007")</c> → hash branch → returns Acme.</item>
    ///   <item><c>Search("20000")</c> → name branch only → no <c>Denumire</c> contains it → empty.</item>
    ///   <item><c>Search("Acme")</c> → name branch only → returns Acme.</item>
    ///   <item><c>Search("  2000000000007  ")</c> → hash branch (Trim+ToUpper canonicalized by the hasher) → Acme.</item>
    /// </list>
    /// </remarks>
    /// <param name="denumireOrIdno">
    /// Free-form query. <c>null</c> / empty / whitespace returns all active contributors.
    /// </param>
    /// <param name="page">Page number (1-based) and size; clamped to 1..200 for size.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paged result ordered by <c>Denumire</c> ascending.</returns>
    public async Task<Result<PagedResult<ContributorListItem>>> SearchAsync(
        string? denumireOrIdno,
        PageRequest page,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var pageSize = Math.Clamp(page.PageSize, 1, 200);
        var pageNumber = Math.Max(1, page.Page);
        var skip = (pageNumber - 1) * pageSize;

        IQueryable<Contributor> query = _db.Contributors.Where(c => c.IsActive);

        if (!string.IsNullOrWhiteSpace(denumireOrIdno))
        {
            var trimmed = denumireOrIdno.Trim();

            if (IsLikelyNationalIdentifier(trimmed))
            {
                // Full 13-digit identifier: equality match against the IdnoHash shadow column.
                // The plaintext Idno column is encrypted (different ciphertext per row), so a
                // SQL/EF substring operator cannot resolve it. The hash is canonicalized by
                // IDeterministicHasher (Trim + ToUpperInvariant), so we can hand the raw
                // trimmed input straight in — no double-canonicalization at the call site.
                // OR-combined with a Denumire substring so a 13-digit string that happens to
                // appear in a legal name still surfaces. The == predicate translates on every
                // EF provider — no in-memory vs Npgsql seam needed for this branch.
                var idnoHash = _idHasher.ComputeHash(trimmed);
                // R0162 / CF 03.13 — diacritic-insensitive search. The relational path uses
                // unaccent(col) ILIKE unaccent(pattern); the InMemory path folds with
                // DiacriticFolding.Fold on both sides. IDNO is ASCII-only so the hash branch
                // does NOT need folding.
                // R0164 / UI 012 / CF 03.02 — wildcard-mask translation. The user's query
                // is FOLDED first (R0162) then run through WildcardMask which translates
                // '*' → '%' (LIKE) / '.*' (regex) and escapes any literal LIKE wildcards.
                // Order matters: fold leaves '*' alone so the wildcard processor sees it.
                var folded = DiacriticFolding.Fold(trimmed);
                var likeFolded = WildcardMask.ToLikePattern(folded);
                if (IsRelationalProvider(_db))
                {
                    query = query.Where(c =>
                        c.IdnoHash == idnoHash ||
                        EF.Functions.ILike(CnasDbFunctions.Unaccent(c.Denumire), likeFolded));
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
                    query = query.Where(c =>
                        c.IdnoHash == idnoHash ||
                        regex.IsMatch(DiacriticFolding.Fold(c.Denumire)));
                }
            }
            else
            {
                // Not a full identifier: substring search on the unencrypted name field only.
                // Partial-IDNO search is intentionally unsupported — the encrypted column
                // cannot answer LIKE queries. The provider seam wraps ONLY this branch
                // because EF.Functions.ILike is Postgres-specific.
                // R0162 / CF 03.13 — diacritic-insensitive (see above for the rationale).
                // R0164 / UI 012 / CF 03.02 — wildcard mask (see above for the rationale).
                var folded = DiacriticFolding.Fold(trimmed);
                var likeFolded = WildcardMask.ToLikePattern(folded);
                if (IsRelationalProvider(_db))
                {
                    query = query.Where(c => EF.Functions.ILike(CnasDbFunctions.Unaccent(c.Denumire), likeFolded));
                }
                else
                {
                    // R0162 + R0164 InMemory fallback — see paired branch above for rationale.
                    var regex = WildcardMask.ToRegex(folded);
                    query = query.Where(c => regex.IsMatch(DiacriticFolding.Fold(c.Denumire)));
                }
            }
        }

        query = query.OrderBy(c => c.Denumire);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await query
            .Skip(skip).Take(pageSize)
            .Select(c => new ContributorListItem(
                _sqids.Encode(c.Id),
                c.Idno,
                c.Denumire,
                c.IsInsolvent))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Result<PagedResult<ContributorListItem>>.Success(
            new PagedResult<ContributorListItem>(rows, pageNumber, pageSize, total));
    }

    /// <inheritdoc />
    public Task<Result> MarkInsolventAsync(string id, CancellationToken ct = default) =>
        FlipInsolvencyAsync(id, makeInsolvent: true, EvtInsolventSet, ct);

    /// <inheritdoc />
    public Task<Result> MarkSolventAsync(string id, CancellationToken ct = default) =>
        FlipInsolvencyAsync(id, makeInsolvent: false, EvtSolventRestored, ct);

    /// <inheritdoc />
    public async Task<Result> DeactivateAsync(string id, CancellationToken ct = default)
    {
        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var entity = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == decoded.Value && c.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        var now = _clock.UtcNow;
        entity.IsActive = false;
        entity.DeregisteredAtUtc = now;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0502 / SEC-AUDIT — record the IDNO HASH on the audit row, never the raw IDNO.
        await _audit.RecordAsync(
            EvtDeactivated,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Contributor),
            entity.Id,
            JsonSerializer.Serialize(new { idnoHash = entity.IdnoHash }, CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<IsInsuredResult>> IsInsuredAsync(string idno, DateTime atUtc, CancellationToken ct = default)
    {
        var idnoResult = Idno.TryCreate(idno);
        if (idnoResult.IsFailure)
        {
            return Result<IsInsuredResult>.Failure(idnoResult.ErrorCode!, idnoResult.ErrorMessage!);
        }
        var canonical = idnoResult.Value.Value;
        // Equality on the hash shadow column — see GetByIdnoAsync for the rationale.
        var canonicalHash = _idHasher.ComputeHash(canonical);

        // A contributor is "insured" at instant `atUtc` if it exists, is active, and was
        // either never de-registered OR was de-registered AFTER the as-of moment.
        var isInsured = await _db.Contributors
            .AnyAsync(c =>
                c.IdnoHash == canonicalHash &&
                c.IsActive &&
                (c.DeregisteredAtUtc == null || c.DeregisteredAtUtc > atUtc),
                ct)
            .ConfigureAwait(false);

        return Result<IsInsuredResult>.Success(new IsInsuredResult(canonical, isInsured, atUtc));
    }

    /// <summary>
    /// Shared body for <see cref="MarkInsolventAsync"/> / <see cref="MarkSolventAsync"/>.
    /// Loads the contributor, flips the flag, bumps audit columns, and journals a
    /// <see cref="AuditSeverity.Critical"/> event so the change is mirrored to MLog.
    /// </summary>
    /// <param name="id">Sqid-encoded contributor id.</param>
    /// <param name="makeInsolvent">Target value for the <c>IsInsolvent</c> flag.</param>
    /// <param name="auditEventCode">Audit event code to record on the journal.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<Result> FlipInsolvencyAsync(string id, bool makeInsolvent, string auditEventCode, CancellationToken ct)
    {
        var decoded = _sqids.TryDecode(id);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var entity = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == decoded.Value && c.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        var now = _clock.UtcNow;
        entity.IsInsolvent = makeInsolvent;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0502 / SEC-AUDIT — record the IDNO HASH on the audit row, never the raw IDNO.
        await _audit.RecordAsync(
            auditEventCode,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Contributor),
            entity.Id,
            JsonSerializer.Serialize(new { idnoHash = entity.IdnoHash, isInsolvent = makeInsolvent }, CachedJsonOptions),
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        return Result.Success();
    }

    // ─────────────────────── R0305 — Annex 1 Business Processes ───────────────────────

    /// <inheritdoc />
    public async Task<Result<ContributorOutput>> UpdateAttributesAsync(
        long contributorId,
        ContributorAttributesUpdateDto input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entity = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result<ContributorOutput>.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        // BP 1.2 cannot mutate a deactivated or merged row — the operator must reactivate
        // via BP 1.4 first (or, for merged rows, mutate the survivor instead).
        if (entity.IsDeactivated)
        {
            return Result<ContributorOutput>.Failure(
                ErrorCodes.Conflict,
                "Contributor is deactivated; reactivate before updating.");
        }

        var now = _clock.UtcNow;
        entity.Denumire = input.Denumire;
        entity.CfojCode = input.CfojCode;
        entity.CaemCode = input.CaemCode;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new { contributorSqid = _sqids.Encode(entity.Id), entity.Denumire, entity.CfojCode, entity.CaemCode },
            CachedJsonOptions);
        await _audit.RecordAsync(
            EvtUpdated,
            AuditSeverity.Notice,
            _caller.UserSqid ?? "?",
            nameof(Contributor),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ContributorBpInvoked.Add(1, new KeyValuePair<string, object?>("bp", "UpdateAttributes"));
        return Result<ContributorOutput>.Success(ToOutput(entity));
    }

    /// <inheritdoc />
    public async Task<Result> DeactivateAsync(long contributorId, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var entity = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }
        if (entity.IsDeactivated)
        {
            return Result.Failure(ErrorCodes.Conflict, "Contributor is already deactivated.");
        }

        var now = _clock.UtcNow;
        entity.IsDeactivated = true;
        entity.DeactivatedAtUtc = now;
        entity.DeactivationReason = reason;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                contributorSqid = _sqids.Encode(entity.Id),
                oldState = "Active",
                newState = "Deactivated",
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            EvtDeactivatedBp,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Contributor),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ContributorBpInvoked.Add(1, new KeyValuePair<string, object?>("bp", "Deactivate"));
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ReactivateAsync(long contributorId, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var entity = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        // Terminal-state guard — once deceased/dissolved/merged the row is read-only.
        if (entity.IsDeceased || entity.IsDissolved || entity.MergedIntoContributorId is not null)
        {
            return Result.Failure(
                ErrorCodes.Conflict,
                "Contributor is in a terminal state and cannot be reactivated.");
        }
        if (!entity.IsDeactivated)
        {
            return Result.Failure(ErrorCodes.Conflict, "Contributor is not deactivated.");
        }

        var now = _clock.UtcNow;
        entity.IsDeactivated = false;
        entity.DeactivatedAtUtc = null;
        entity.DeactivationReason = null;
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                contributorSqid = _sqids.Encode(entity.Id),
                oldState = "Deactivated",
                newState = "Active",
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            EvtReactivated,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Contributor),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ContributorBpInvoked.Add(1, new KeyValuePair<string, object?>("bp", "Reactivate"));
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> MergeDuplicatesAsync(
        long duplicateContributorId,
        long survivorContributorId,
        CancellationToken ct = default)
    {
        if (duplicateContributorId == survivorContributorId)
        {
            return Result.Failure(ErrorCodes.ValidationFailed, "Duplicate and survivor must differ.");
        }

        var duplicate = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == duplicateContributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (duplicate is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Duplicate contributor not found.");
        }

        var survivor = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == survivorContributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (survivor is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Survivor contributor not found.");
        }

        // Either side already merged → refuse. The duplicate cannot be re-merged, and the
        // survivor having been merged would create a chain that breaks the invariant
        // "MergedIntoContributorId points at a non-merged row".
        if (duplicate.MergedIntoContributorId is not null || survivor.MergedIntoContributorId is not null)
        {
            return Result.Failure(
                ErrorCodes.Forbidden,
                "Merge refused: one of the rows is already merged.");
        }

        var survivorSqid = _sqids.Encode(survivor.Id);
        var now = _clock.UtcNow;
        duplicate.MergedIntoContributorId = survivor.Id;
        duplicate.IsDeactivated = true;
        duplicate.DeactivatedAtUtc = now;
        duplicate.DeactivationReason = $"merged into {survivorSqid}";
        duplicate.UpdatedAtUtc = now;
        duplicate.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                duplicateSqid = _sqids.Encode(duplicate.Id),
                survivorSqid,
                oldState = "Active",
                newState = "Merged",
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            EvtMerged,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Contributor),
            duplicate.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ContributorBpInvoked.Add(1, new KeyValuePair<string, object?>("bp", "Merge"));
        return Result.Success();
    }

    /// <inheritdoc />
    public Task<Result> SplitAsync(
        long sourceContributorId,
        ContributorSplitInputDto input,
        CancellationToken ct = default)
    {
        // BP 1.6 — Deferred-by-design. The split criteria depend on specialist tooling
        // we do not have today; the controller surfaces this as HTTP 501.
        _ = sourceContributorId;
        _ = input;
        _ = ct;
        CnasMeter.ContributorBpInvoked.Add(1, new KeyValuePair<string, object?>("bp", "Split"));
        return Task.FromResult(Result.Failure(
            ErrorCodes.NotImplemented,
            "CONTRIBUTOR_SPLIT_NOT_IMPLEMENTED"));
    }

    /// <inheritdoc />
    public async Task<Result> AdminCorrectAsync(
        long contributorId,
        string fieldName,
        string oldValueHash,
        string newValueHash,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        ArgumentNullException.ThrowIfNull(oldValueHash);
        ArgumentNullException.ThrowIfNull(newValueHash);
        ArgumentNullException.ThrowIfNull(reason);

        // Permission gate — defense-in-depth (the controller / authorization policy is
        // the primary gate, but the service re-checks so an internal caller cannot
        // bypass the BP 1.7 permission contract).
        if (!_caller.Roles.Contains(PermAdminCorrect))
        {
            return Result.Failure(ErrorCodes.Forbidden, "Caller lacks Contributor.AdminCorrect permission.");
        }

        var entity = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        // BP 1.7 records the audit ONLY — the actual field write happens through BP 1.2
        // or R0301 child-table services. This keeps the audit row authoritative even
        // when the write fails or is routed elsewhere.
        var details = JsonSerializer.Serialize(
            new
            {
                contributorSqid = _sqids.Encode(entity.Id),
                fieldName,
                oldValueHash,
                newValueHash,
                reason,
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            EvtAdminCorrection,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Contributor),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ContributorBpInvoked.Add(1, new KeyValuePair<string, object?>("bp", "AdminCorrect"));
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> MarkDeceasedOrDissolvedAsync(
        long contributorId,
        DateOnly effectiveDate,
        CancellationToken ct = default)
    {
        var entity = await _db.Contributors
            .SingleOrDefaultAsync(c => c.Id == contributorId && c.IsActive, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Contributor not found.");
        }

        // Already terminal — no double-flip, no second audit row.
        if (entity.IsDeceased || entity.IsDissolved)
        {
            return Result.Failure(
                ErrorCodes.Conflict,
                "Contributor is already in a terminal deceased/dissolved state.");
        }

        // NaturalPerson vs LegalPerson by the IDNO leading digit. Moldovan IDNP (natural
        // persons) starts with 0/1/2; IDNO (legal persons) starts with 1-9 generally and
        // is distinguished by the upstream RSP/RSUD register. For this BP we treat the
        // leading digit as a sufficient proxy: 0/2 → natural; 1+ → legal. The Idno value
        // object's TryCreate confirms the 13-digit shape AND mod-10 checksum at register
        // time, so by the time we reach this BP the column is well-formed.
        var leading = entity.Idno.Length > 0 ? entity.Idno[0] : '0';
        var isNatural = leading is '0' or '2';

        var now = _clock.UtcNow;
        var effectiveUtc = effectiveDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (isNatural)
        {
            entity.IsDeceased = true;
            entity.DeceasedAtUtc = effectiveUtc;
        }
        else
        {
            entity.IsDissolved = true;
            entity.DissolvedAtUtc = effectiveUtc;
        }

        entity.IsDeactivated = true;
        entity.DeactivatedAtUtc = now;
        entity.DeactivationReason = isNatural ? "deceased" : "dissolved";
        entity.UpdatedAtUtc = now;
        entity.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var details = JsonSerializer.Serialize(
            new
            {
                contributorSqid = _sqids.Encode(entity.Id),
                oldState = "Active",
                newState = isNatural ? "Deceased" : "Dissolved",
                effectiveDate = effectiveDate.ToString("O"),
            },
            CachedJsonOptions);
        await _audit.RecordAsync(
            EvtDeceasedOrDissolved,
            AuditSeverity.Critical,
            _caller.UserSqid ?? "?",
            nameof(Contributor),
            entity.Id,
            details,
            _caller.SourceIp,
            _caller.CorrelationId,
            ct).ConfigureAwait(false);

        CnasMeter.ContributorBpInvoked.Add(1, new KeyValuePair<string, object?>("bp", "MarkDeceasedOrDissolved"));
        return Result.Success();
    }

    /// <summary>Maps a <see cref="Contributor"/> entity to its outbound DTO with Sqid id.</summary>
    /// <param name="entity">Loaded entity (must not be null).</param>
    /// <returns>Sqid-encoded output projection.</returns>
    private ContributorOutput ToOutput(Contributor entity) => new(
        _sqids.Encode(entity.Id),
        entity.Idno,
        entity.Denumire,
        entity.CfojCode,
        entity.CaemCode,
        entity.IsInsolvent,
        entity.RegisteredAtUtc,
        entity.DeregisteredAtUtc);

    /// <summary>
    /// Detects whether the underlying <see cref="ICnasDbContext"/> is backed by a relational
    /// provider (Npgsql in production) vs the in-memory test fake. This is the single seam
    /// that lets the search query stay native PostgreSQL ILIKE in production while remaining
    /// executable against EF Core InMemory in integration tests.
    /// </summary>
    /// <remarks>
    /// The seam wraps ONLY the name-field substring branch of <see cref="SearchAsync"/>;
    /// the IDNO-hash equality branch uses a universal <c>==</c> predicate that translates
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
    /// Used by <see cref="SearchAsync"/> to decide whether to take the IDNO-hash equality
    /// branch or fall back to the name-field substring branch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a SHAPE detection only — it does NOT validate the mod-10 checksum.
    /// We deliberately do not invoke <see cref="Cnas.Ps.Core.ValueObjects.Idno.TryCreate"/>:
    /// a search query is a user-typed string, not a registration input, and an invalid
    /// checksum should return "no results" rather than "invalid IDNO" (the user would
    /// have no way to know whether they mistyped a digit or whether the row doesn't exist).
    /// The hash branch will simply find zero rows for a typo'd 13-digit string — that is
    /// the correct user-visible behaviour.
    /// </para>
    /// <para>
    /// Inputs with non-digit characters (e.g. <c>"20000abc"</c>, <c>"Doe"</c>) MUST return
    /// false here so they are routed to the name-substring branch — extracting and hashing
    /// only the digits would re-introduce the partial-IDNO bug we are intentionally NOT
    /// supporting in this batch.
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
