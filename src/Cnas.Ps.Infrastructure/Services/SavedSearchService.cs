using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Default <see cref="ISavedSearchService"/> implementation backed by
/// <see cref="ICnasDbContext"/>. Implements the R0165 / CF 03.06 contract — see the
/// interface XML doc for the access rules and the limit codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent create.</b> The natural-key triple <c>(OwnerUserId, Registry, Name)</c>
/// is unique by DB index. A duplicate <see cref="CreateAsync"/> on the same triple
/// returns the existing row's Sqid WITHOUT writing — same shape as the duplicate-detect
/// idempotency used by other CNAS services (e.g. application submission with the same
/// solicitant). Callers that want to mutate fields go through
/// <see cref="UpdateAsync"/> instead.
/// </para>
/// <para>
/// <b>Audit shape.</b> Each successful create / update / delete emits a row through
/// <see cref="IAuditService"/> with the <c>SAVED_SEARCH.{CREATED|UPDATED|DELETED}</c>
/// event code at <see cref="AuditSeverity.Notice"/>. <c>DetailsJson</c> carries
/// <c>{ "registry": "...", "name": "..." }</c> only — the filter payload is NEVER
/// included so a citizen's IDNP inadvertently captured in a filter term cannot leak
/// through the audit trail.
/// </para>
/// <para>
/// <b>Counter contract.</b> <see cref="CnasMeter.SavedSearchSaved"/> is incremented once
/// per fresh create AND once per update. Idempotent create that returns an existing
/// row's Sqid does NOT increment because no new save took place. Delete does not
/// increment because the counter measures "save" operations specifically.
/// </para>
/// </remarks>
public sealed class SavedSearchService(
    ICnasDbContext db,
    ICallerContext caller,
    ISqidService sqids,
    ICnasTimeProvider clock,
    IAuditService audit,
    IOptions<SavedSearchOptions> options)
    : ISavedSearchService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICallerContext _caller = caller;
    private readonly ISqidService _sqids = sqids;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;
    private readonly SavedSearchOptions _opts = options.Value;

    /// <summary>Stable event-code prefix for the audit trail. Three flavours are emitted.</summary>
    private const string AuditPrefix = "SAVED_SEARCH";

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<SavedSearchItem>>> ListAsync(string registry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registry))
        {
            return Result<IReadOnlyList<SavedSearchItem>>.Failure(
                ErrorCodes.ValidationFailed, "Registry is required.");
        }
        if (_caller.UserId is not long callerId)
        {
            return Result<IReadOnlyList<SavedSearchItem>>.Failure(
                ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // R0524: this method is now strictly "list mine" — only caller-owned rows. The
        // broader union (owned + Shared + Group-where-member) lives on
        // ListAccessibleAsync.
        var raw = await _db.SavedSearches
            .Where(s => s.IsActive
                && s.Registry == registry
                && s.OwnerUserId == callerId)
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                s.Registry,
                s.Name,
                s.FilterJson,
                s.IsShared,
                s.OwnerUserId,
                s.SharingScope,
                s.SharedWithGroupCode,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var items = raw
            .Select(r => new SavedSearchItem(
                _sqids.Encode(r.Id),
                r.Registry,
                r.Name,
                r.FilterJson,
                r.IsShared,
                _sqids.Encode(r.OwnerUserId),
                r.SharingScope.ToString(),
                r.SharedWithGroupCode))
            .ToList();

        return Result<IReadOnlyList<SavedSearchItem>>.Success(items);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedSearchItem>> ListAccessibleAsync(string registry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(registry))
        {
            // R0524: callers that hit this with whitespace get an empty list rather
            // than an exception — the method has no Result envelope and the API surface
            // already guards the query parameter. Defence-in-depth.
            return Array.Empty<SavedSearchItem>();
        }
        if (_caller.UserId is not long callerId)
        {
            // Anonymous callers should never reach here (controller is [Authorize]),
            // but if they do the safe answer is "you can see nothing".
            return Array.Empty<SavedSearchItem>();
        }

        // R0524: resolve the caller's group memberships from their UserProfile row.
        // We do this in a single query against the persistence boundary because
        // ICallerContext does not (yet) expose Groups directly — adding it would
        // require updating ~30 mock harnesses across the test suite. The lookup is
        // cheap (PK by user-id) and lives inside the same DbContext scope as the
        // accessible-rows query that follows.
        var groups = await _db.UserProfiles
            .Where(u => u.Id == callerId && u.IsActive)
            .Select(u => u.Groups)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false) ?? new List<string>();

        // Build the access predicate. Owned rows always; Shared rows by anyone; Group
        // rows whose code is in the caller's group set. The Group branch is gated by
        // "groups.Any()" so callers with no groups don't widen the result to every
        // Group-scoped row with a NULL or empty group-code.
        var rows = await _db.SavedSearches
            .Where(s => s.IsActive
                && s.Registry == registry
                && (
                    s.OwnerUserId == callerId
                    || s.SharingScope == SavedSearchSharingScope.Shared
                    || (s.SharingScope == SavedSearchSharingScope.Group
                        && s.SharedWithGroupCode != null
                        && groups.Contains(s.SharedWithGroupCode))
                ))
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                s.Registry,
                s.Name,
                s.FilterJson,
                s.IsShared,
                s.OwnerUserId,
                s.SharingScope,
                s.SharedWithGroupCode,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => new SavedSearchItem(
                _sqids.Encode(r.Id),
                r.Registry,
                r.Name,
                r.FilterJson,
                r.IsShared,
                _sqids.Encode(r.OwnerUserId),
                r.SharingScope.ToString(),
                r.SharedWithGroupCode))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<Result<SavedSearchItem>> GetAsync(string sqid, CancellationToken ct = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Result<SavedSearchItem>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<SavedSearchItem>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.SavedSearches
            .SingleOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<SavedSearchItem>.Failure(ErrorCodes.NotFound, "Saved search not found.");
        }

        // Access rule: owner OR shared. Otherwise 403. A non-owner reading a non-shared
        // row gets Forbidden rather than NotFound so the UI can distinguish "doesn't
        // exist" from "you can't see this".
        if (row.OwnerUserId != callerId && !row.IsShared)
        {
            return Result<SavedSearchItem>.Failure(ErrorCodes.Forbidden, "Saved search is private to its owner.");
        }

        return Result<SavedSearchItem>.Success(Project(row));
    }

    /// <inheritdoc />
    public async Task<Result<string>> CreateAsync(SavedSearchCreateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is not long ownerId)
        {
            return Result<string>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        if (ValidateInputs(input.Registry, input.Name, input.FilterJson) is { } validation)
        {
            return Result<string>.Failure(validation.code, validation.message);
        }

        // Idempotent-create: a same-triple row already owned by the caller is returned
        // verbatim without writing. This shape preserves the natural-key uniqueness
        // contract at the service surface (so callers don't have to handle a "the row
        // exists" error) and avoids burning a counter increment on a no-op.
        var existing = await _db.SavedSearches
            .SingleOrDefaultAsync(
                s => s.IsActive
                  && s.OwnerUserId == ownerId
                  && s.Registry == input.Registry
                  && s.Name == input.Name,
                ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Result<string>.Success(_sqids.Encode(existing.Id));
        }

        // Per-owner cap — defended at the service layer so a UI bug spamming create
        // cannot wedge a single user's namespace by exhausting the natural-key triple.
        // The cap counts only active rows; soft-deleted rows are excluded.
        var ownedCount = await _db.SavedSearches
            .CountAsync(s => s.IsActive && s.OwnerUserId == ownerId, ct)
            .ConfigureAwait(false);
        if (ownedCount >= _opts.MaxPerOwner)
        {
            return Result<string>.Failure(
                ErrorCodes.SavedSearchLimitReached,
                $"Owner already holds {ownedCount} active saved searches (cap {_opts.MaxPerOwner}).");
        }

        var now = _clock.UtcNow;
        var row = new SavedSearch
        {
            OwnerUserId = ownerId,
            Registry = input.Registry,
            Name = input.Name,
            FilterJson = input.FilterJson,
            IsShared = input.IsShared,
            // R0524: keep SharingScope aligned with the legacy IsShared flag on create.
            // Pre-R0524 callers continue to flip the binary flag and the new column
            // tracks them automatically. Group-scope sharing requires the explicit
            // ShareAsync path.
            SharingScope = input.IsShared ? SavedSearchSharingScope.Shared : SavedSearchSharingScope.Private,
            SharedWithGroupCode = null,
            CreatedAtUtc = now,
            CreatedBy = _caller.UserSqid,
            IsActive = true,
        };
        _db.SavedSearches.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CnasMeter.SavedSearchSaved.Add(1);

        await EmitAuditAsync($"{AuditPrefix}.CREATED", row, ct).ConfigureAwait(false);

        return Result<string>.Success(_sqids.Encode(row.Id));
    }

    /// <inheritdoc />
    public async Task<Result> UpdateAsync(string sqid, SavedSearchUpdateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is not long callerId)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.SavedSearches
            .SingleOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Saved search not found.");
        }

        // Only the owner may mutate — non-owners with read access (IsShared) explicitly
        // get Forbidden so a shared-row reader cannot stealth-edit through the API.
        if (row.OwnerUserId != callerId)
        {
            return Result.Failure(ErrorCodes.Forbidden, "Only the owner may update a saved search.");
        }

        if (ValidateInputs(row.Registry, input.Name, input.FilterJson) is { } validation)
        {
            return Result.Failure(validation.code, validation.message);
        }

        // Name uniqueness: if the new name collides with another active row owned by the
        // caller in the same registry (excluding this one) we reject up-front with a
        // structured error rather than letting the DB unique index throw on save.
        if (!string.Equals(row.Name, input.Name, StringComparison.Ordinal))
        {
            var conflict = await _db.SavedSearches
                .AnyAsync(s => s.IsActive
                    && s.OwnerUserId == callerId
                    && s.Registry == row.Registry
                    && s.Name == input.Name
                    && s.Id != row.Id, ct)
                .ConfigureAwait(false);
            if (conflict)
            {
                return Result.Failure(
                    ErrorCodes.ValidationFailed,
                    "Another saved search with this name already exists on the same registry.");
            }
        }

        var now = _clock.UtcNow;
        row.Name = input.Name;
        row.FilterJson = input.FilterJson;
        row.IsShared = input.IsShared;
        // R0524: keep the granular SharingScope aligned with the legacy IsShared flag
        // on UpdateAsync — preserves an existing Group scope only when the caller is
        // not also changing IsShared. The semantics: if the caller flips IsShared on
        // UpdateAsync they intend the binary scope (Private/Shared); if they want to
        // pin to Group they must use the dedicated ShareAsync path.
        if (input.IsShared)
        {
            row.SharingScope = SavedSearchSharingScope.Shared;
            row.SharedWithGroupCode = null;
        }
        else if (row.SharingScope == SavedSearchSharingScope.Shared)
        {
            // IsShared = false on Update collapses a Shared row back to Private; a
            // previously Group-scoped row keeps its scope (UpdateAsync does not own
            // the Group dimension — only ShareAsync does).
            row.SharingScope = SavedSearchSharingScope.Private;
            row.SharedWithGroupCode = null;
        }
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        CnasMeter.SavedSearchSaved.Add(1);

        await EmitAuditAsync($"{AuditPrefix}.UPDATED", row, ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string sqid, CancellationToken ct = default)
    {
        if (_caller.UserId is not long callerId)
        {
            return Result.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.SavedSearches
            .SingleOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ErrorCodes.NotFound, "Saved search not found.");
        }

        if (row.OwnerUserId != callerId)
        {
            return Result.Failure(ErrorCodes.Forbidden, "Only the owner may delete a saved search.");
        }

        var now = _clock.UtcNow;
        row.IsActive = false;
        row.UpdatedAtUtc = now;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync($"{AuditPrefix}.DELETED", row, ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result<SavedSearchItem>> ShareAsync(string sqid, SavedSearchShareInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_caller.UserId is not long callerId)
        {
            return Result<SavedSearchItem>.Failure(ErrorCodes.Unauthorized, "Caller is not authenticated.");
        }

        // Defence-in-depth scope parse — the API-level validator already rejects bad
        // strings but the service must not blindly trust ToString() round-trips.
        if (string.IsNullOrWhiteSpace(input.SharingScope)
            || !Enum.TryParse<SavedSearchSharingScope>(input.SharingScope, ignoreCase: false, out var scope))
        {
            return Result<SavedSearchItem>.Failure(
                ErrorCodes.ValidationFailed,
                "SharingScope must be one of: Private, Shared, Group.");
        }

        // Cross-field invariant — mirrors the validator. Group REQUIRES a group code;
        // Private / Shared REQUIRE null. Defence-in-depth.
        var groupCode = string.IsNullOrWhiteSpace(input.SharedWithGroupCode) ? null : input.SharedWithGroupCode;
        if (scope == SavedSearchSharingScope.Group && groupCode is null)
        {
            return Result<SavedSearchItem>.Failure(
                ErrorCodes.ValidationFailed,
                "SharedWithGroupCode is required when SharingScope = Group.");
        }
        if (scope != SavedSearchSharingScope.Group && groupCode is not null)
        {
            return Result<SavedSearchItem>.Failure(
                ErrorCodes.ValidationFailed,
                "SharedWithGroupCode must be null when SharingScope ≠ Group.");
        }

        var decoded = _sqids.TryDecode(sqid);
        if (decoded.IsFailure)
        {
            return Result<SavedSearchItem>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        var row = await _db.SavedSearches
            .SingleOrDefaultAsync(s => s.Id == decoded.Value && s.IsActive, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return Result<SavedSearchItem>.Failure(ErrorCodes.NotFound, "Saved search not found.");
        }

        // Only the owner may flip sharing. Non-owner shared-reader callers are
        // explicitly rejected here so a Shared row cannot be widened (or narrowed
        // back to Private) by anyone but its owner.
        if (row.OwnerUserId != callerId)
        {
            return Result<SavedSearchItem>.Failure(
                ErrorCodes.Forbidden,
                "Only the owner may change the sharing scope of a saved search.");
        }

        // Apply. Keep the legacy IsShared flag in sync so the pre-R0524 readers
        // (controllers / DTO consumers that have not migrated to SharingScope) keep
        // returning the right answer.
        row.SharingScope = scope;
        row.SharedWithGroupCode = groupCode;
        row.IsShared = scope == SavedSearchSharingScope.Shared;
        row.UpdatedAtUtc = _clock.UtcNow;
        row.UpdatedBy = _caller.UserSqid;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Audit — Notice severity because sharing is a write to non-sensitive metadata.
        // The DetailsJson carries the scope + group code so an investigator can see
        // who shared what without joining against the row itself.
        var details = JsonSerializer.Serialize(new
        {
            scope = scope.ToString(),
            groupCode,
        });
        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: $"{AuditPrefix}.SHARED",
            severity: AuditSeverity.Notice,
            actorId: actor,
            targetEntity: nameof(SavedSearch),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);

        return Result<SavedSearchItem>.Success(Project(row));
    }

    /// <summary>
    /// Validates the common length budgets for create/update. Returns <c>null</c> when
    /// every check passes; otherwise a structured error code + message ready to lift
    /// into a <see cref="Result"/> or <see cref="Result{T}"/>.
    /// </summary>
    /// <param name="registry">Registry code; must be non-empty.</param>
    /// <param name="name">Friendly name; must be non-empty and within <see cref="SavedSearchOptions.MaxNameLength"/>.</param>
    /// <param name="filterJson">JSON filter; must be non-empty and within <see cref="SavedSearchOptions.MaxFilterJsonLength"/> bytes.</param>
    /// <returns><c>null</c> on success; otherwise the failure pair.</returns>
    private (string code, string message)? ValidateInputs(string? registry, string? name, string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(registry))
        {
            return (ErrorCodes.ValidationFailed, "Registry is required.");
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            return (ErrorCodes.ValidationFailed, "Name is required.");
        }
        if (name.Length > _opts.MaxNameLength)
        {
            return (ErrorCodes.ValidationFailed, $"Name exceeds the {_opts.MaxNameLength}-character cap.");
        }
        if (string.IsNullOrWhiteSpace(filterJson))
        {
            return (ErrorCodes.ValidationFailed, "FilterJson is required.");
        }
        if (filterJson.Length > _opts.MaxFilterJsonLength)
        {
            return (ErrorCodes.ValidationFailed, $"FilterJson exceeds the {_opts.MaxFilterJsonLength}-byte cap.");
        }
        return null;
    }

    /// <summary>
    /// Projects the entity into its output DTO with Sqid-encoded identifiers. Centralised
    /// so the encoding rule is applied identically across Get / List / Share.
    /// </summary>
    /// <param name="row">Loaded entity row.</param>
    /// <returns>The DTO the API surface returns.</returns>
    private SavedSearchItem Project(SavedSearch row) => new(
        _sqids.Encode(row.Id),
        row.Registry,
        row.Name,
        row.FilterJson,
        row.IsShared,
        _sqids.Encode(row.OwnerUserId),
        row.SharingScope.ToString(),
        row.SharedWithGroupCode);

    /// <summary>
    /// Emits an audit-trail row for a saved-search create/update/delete. The
    /// <c>DetailsJson</c> carries the registry + name only — never the FilterJson —
    /// because a citizen's identifier inadvertently captured in a filter must not leak
    /// through the audit trail.
    /// </summary>
    /// <param name="eventCode">Stable event code (e.g. <c>SAVED_SEARCH.CREATED</c>).</param>
    /// <param name="row">The persisted (or just-modified) row.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task EmitAuditAsync(string eventCode, SavedSearch row, CancellationToken ct)
    {
        // SECurity-conscious payload: registry + name only. The PII redactor (R0185) is
        // a defence-in-depth net; the contract here is that we don't include the body
        // in the first place.
        var details = JsonSerializer.Serialize(new
        {
            registry = row.Registry,
            name = row.Name,
        });

        // Use the caller's Sqid when available; fall back to "system" for background
        // contexts. The audit row always carries the numeric TargetEntityId so an
        // investigator can join against the saved-search row directly.
        var actor = _caller.UserSqid ?? "system";
        await _audit.RecordAsync(
            eventCode: eventCode,
            severity: AuditSeverity.Notice,
            actorId: actor,
            targetEntity: nameof(SavedSearch),
            targetEntityId: row.Id,
            detailsJson: details,
            sourceIp: _caller.SourceIp,
            correlationId: _caller.CorrelationId,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
