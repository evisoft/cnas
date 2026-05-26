using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// EF Core–backed <see cref="IUserDirectoryService"/>. Looks up profiles by the MPass
/// subject claim (mapped to <see cref="UserProfile.MPassSubject"/>) and either creates a
/// new soft-deletable row or refreshes mutable fields on the existing row.
/// </summary>
/// <remarks>
/// Called from the OIDC <c>OnTokenValidated</c> event. The handler must NOT propagate
/// exceptions — sign-in is more important than the profile-sync side-effect; transient
/// faults are retried implicitly on the user's next visit.
/// </remarks>
/// <param name="db">EF Core context abstraction (scoped per request).</param>
/// <param name="clock">UTC clock — never <see cref="DateTime.UtcNow"/> directly (CLAUDE.md).</param>
/// <param name="audit">Audit journal façade for the <c>USER_DIRECTORY.SIGN_IN_SYNC</c> event.</param>
public sealed class UserDirectoryService(
    ICnasDbContext db,
    ICnasTimeProvider clock,
    IAuditService audit) : IUserDirectoryService
{
    private readonly ICnasDbContext _db = db;
    private readonly ICnasTimeProvider _clock = clock;
    private readonly IAuditService _audit = audit;

    /// <summary>Stable audit event code emitted on every successful upsert.</summary>
    private const string EvtSignInSync = "USER_DIRECTORY.SIGN_IN_SYNC";

    /// <inheritdoc />
    public async Task<Result<long>> UpsertOnSignInAsync(
        string externalSub,
        string displayName,
        string? email,
        IReadOnlyCollection<string> roles,
        CancellationToken ct = default)
    {
        // 1. Reject anonymous/garbage input at the boundary. The OIDC pipeline is expected
        //    to substitute a placeholder display name when the IdP omits it, so an empty
        //    `displayName` here indicates a programmer bug rather than a runtime user issue.
        if (string.IsNullOrWhiteSpace(externalSub))
        {
            return Result<long>.Failure(ErrorCodes.ValidationFailed, "externalSub is required.");
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Result<long>.Failure(ErrorCodes.ValidationFailed, "displayName is required.");
        }

        ArgumentNullException.ThrowIfNull(roles);

        var now = _clock.UtcNow;

        // 2. Lookup by MPassSubject (== externalSub). Single round-trip; the unique-filter
        //    index on MPassSubject keeps this fast at scale.
        var existing = await _db.UserProfiles
            .SingleOrDefaultAsync(u => u.MPassSubject == externalSub, ct)
            .ConfigureAwait(false);

        // 3a. Hit — refresh mutable identity fields. Account-state gate takes precedence:
        //     a non-Active profile (Locked/Suspended/Disabled per R0059 / SEC 016) must NOT
        //     be silently re-activated by an MPass sign-in, otherwise an administrative
        //     transition could be circumvented by re-authenticating. Soft-deleted rows
        //     (IsActive=false) are likewise rejected — the row remains in the table for
        //     audit retention but cannot back a live session.
        if (existing is not null)
        {
            if (existing.State != UserAccountState.Active || !existing.IsActive)
            {
                return Result<long>.Failure(
                    ErrorCodes.Forbidden,
                    $"User profile is not Active (state={existing.State}).");
            }

            existing.DisplayName = displayName;
            existing.Email = email;
            existing.Roles = [.. roles.Distinct(StringComparer.Ordinal)];
            existing.UpdatedAtUtc = now;
            existing.UpdatedBy = externalSub;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await _audit.RecordAsync(
                EvtSignInSync,
                AuditSeverity.Information,
                externalSub,
                nameof(UserProfile),
                existing.Id,
                $"{{\"action\":\"updated\",\"roles\":{SerializeRoles(existing.Roles)}}}",
                sourceIp: null,
                correlationId: null,
                ct).ConfigureAwait(false);

            return Result<long>.Success(existing.Id);
        }

        // 3b. Miss — create a fresh profile. CreatedBy/CreatedAt are stamped from the
        //     authenticated principal so future audits attribute the row to the right actor.
        //     State defaults to Active (R0059) — explicit assignment kept for documentary
        //     value so a reader does not have to chase the enum default.
        var fresh = new UserProfile
        {
            MPassSubject = externalSub,
            DisplayName = displayName,
            Email = email,
            Roles = [.. roles.Distinct(StringComparer.Ordinal)],
            CreatedAtUtc = now,
            CreatedBy = externalSub,
            IsActive = true,
            State = UserAccountState.Active,
        };
        _db.UserProfiles.Add(fresh);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.RecordAsync(
            EvtSignInSync,
            AuditSeverity.Information,
            externalSub,
            nameof(UserProfile),
            fresh.Id,
            $"{{\"action\":\"created\",\"roles\":{SerializeRoles(fresh.Roles)}}}",
            sourceIp: null,
            correlationId: null,
            ct).ConfigureAwait(false);

        return Result<long>.Success(fresh.Id);
    }

    /// <summary>
    /// Serialises a role list to a compact JSON array literal for embedding in the audit
    /// <c>detailsJson</c>. Manual escaping keeps the dependency surface small and matches
    /// the JSON-as-string convention used by sibling services in this file.
    /// </summary>
    /// <param name="roles">Distinct role codes to serialise.</param>
    /// <returns>A JSON array literal, e.g. <c>["cnas-user","cnas-admin"]</c>.</returns>
    private static string SerializeRoles(IEnumerable<string> roles) =>
        System.Text.Json.JsonSerializer.Serialize(roles);
}
