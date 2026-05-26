using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Security;

/// <summary>
/// <see cref="IRefreshTokenService"/> implementation backing the R0053 token pipeline
/// (CLAUDE.md §5.3 / SEC 018). Mints, rotates, and revokes opaque refresh tokens with
/// rotation + reuse-detection family revoke. The plaintext token is returned to the
/// caller exactly once and NEVER persisted — the store keeps only its SHA-256 hash.
/// </summary>
/// <remarks>
/// <para>
/// <b>Token shape.</b> 48 bytes from <see cref="RandomNumberGenerator.GetBytes(int)"/>,
/// base64url-encoded without padding — 64 ASCII chars carrying 384 bits of entropy.
/// Brute-force preimage attacks against the SHA-256 hash are infeasible at this size.
/// </para>
/// <para>
/// <b>Reuse-detection.</b> When a token presented to <see cref="RotateAsync"/> has a
/// non-null <see cref="RefreshToken.ConsumedAtUtc"/>, the service revokes every live
/// row in the same family (RevokedAtUtc = now, RevokedReason = "reuse-detected") and
/// returns <see cref="ErrorCodes.RefreshTokenReused"/>. The log line carries only the
/// family GUID + numeric user id — never the IDNP, email, or any other PII.
/// </para>
/// <para>
/// <b>Account-state gate.</b> Before issuing a child token, <see cref="RotateAsync"/>
/// re-checks the underlying user's <see cref="UserProfile.State"/>. A non-Active
/// account causes the family to be revoked and the call to return
/// <see cref="ErrorCodes.RefreshTokenRevoked"/> — preventing a suspended / disabled /
/// locked account from minting fresh access tokens via refresh after the access
/// token's 15-minute window elapses.
/// </para>
/// <para>
/// <b>Scoped lifetime.</b> Holds a per-request <see cref="ICnasDbContext"/>; register
/// as Scoped in DI alongside the rest of the service graph.
/// </para>
/// </remarks>
public sealed class RefreshTokenService : IRefreshTokenService
{
    /// <summary>Length in bytes of the random plaintext (48 → 64 base64url chars).</summary>
    private const int TokenByteLength = 48;

    /// <summary>EF Core context — per-request scope.</summary>
    private readonly ICnasDbContext _db;

    /// <summary>Clock abstraction (UTC Everywhere).</summary>
    private readonly ICnasTimeProvider _clock;

    /// <summary>JWT/refresh options snapshot (lifetimes).</summary>
    private readonly JwtOptions _options;

    /// <summary>Logger — used for the reuse-detection WARN line (no PII).</summary>
    private readonly ILogger<RefreshTokenService> _logger;

    /// <summary>
    /// Constructs the refresh-token service. Dependencies are resolved by DI; the
    /// composition root registers the type as Scoped because of the per-request
    /// <see cref="ICnasDbContext"/>.
    /// </summary>
    /// <param name="db">EF Core context.</param>
    /// <param name="clock">System clock abstraction.</param>
    /// <param name="options">JWT options (lifetimes).</param>
    /// <param name="logger">Logger for reuse-detection WARN lines.</param>
    public RefreshTokenService(
        ICnasDbContext db,
        ICnasTimeProvider clock,
        IOptions<JwtOptions> options,
        ILogger<RefreshTokenService> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<RefreshTokenIssueResult>> IssueAsync(long userId, CancellationToken ct = default)
    {
        var (plaintext, hash) = GenerateTokenAndHash();
        var now = _clock.UtcNow;
        var familyId = Guid.NewGuid();
        var expiresAt = now + _options.RefreshTokenLifetime;

        var row = new RefreshToken
        {
            TokenHash = hash,
            FamilyId = familyId,
            ParentTokenId = null,
            UserId = userId,
            IssuedAtUtc = now,
            ExpiresAtUtc = expiresAt,
            CreatedAtUtc = now,
            IsActive = true,
        };
        _db.RefreshTokens.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0040 — count successful issuance AFTER the row is persisted. A SaveChanges
        // throw therefore propagates without inflating the success counter.
        CnasMeter.RefreshIssued.Add(1);

        return Result<RefreshTokenIssueResult>.Success(
            new RefreshTokenIssueResult(plaintext, familyId, expiresAt, userId));
    }

    /// <inheritdoc />
    public async Task<Result<RefreshTokenIssueResult>> RotateAsync(string opaqueRefreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opaqueRefreshToken))
        {
            return Result<RefreshTokenIssueResult>.Failure(
                ErrorCodes.RefreshTokenMissing,
                "Refresh token was not supplied.");
        }

        var hash = Sha256Hex(opaqueRefreshToken);
        var existing = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return Result<RefreshTokenIssueResult>.Failure(
                ErrorCodes.RefreshTokenInvalid,
                "Refresh token is not recognised.");
        }

        var now = _clock.UtcNow;

        // 1. Revoked already — the family was logged out or admin-killed.
        if (existing.RevokedAtUtc is not null)
        {
            return Result<RefreshTokenIssueResult>.Failure(
                ErrorCodes.RefreshTokenRevoked,
                "Refresh token has been revoked.");
        }

        // 2. Expired — outside the 30-day window. Dead, but no family revoke needed.
        if (existing.ExpiresAtUtc <= now)
        {
            return Result<RefreshTokenIssueResult>.Failure(
                ErrorCodes.RefreshTokenExpired,
                "Refresh token has expired.");
        }

        // 3. ALREADY CONSUMED — this is reuse-detection. The legitimate client should
        //    have replaced this token after the prior rotation; presenting it again
        //    means either the client has a bug OR an attacker has a stolen copy. We
        //    revoke every live row in the family so both sides lose access. The log
        //    line carries only the family GUID + numeric user id (no PII).
        if (existing.ConsumedAtUtc is not null)
        {
            await RevokeFamilyRowsAsync(existing.FamilyId, "reuse-detected", now, ct).ConfigureAwait(false);
            // R0040 — the headline security counter. Tag family.revoked=true so the
            // dashboard chart pins both the detection rate AND the revoke confirmation
            // on the same series. PII-safe: no family GUID, no user id in tags — that
            // information already lives in the WARN log line below for forensic
            // follow-up, but must NEVER reach the metrics pipeline (CLAUDE.md §5.6).
            CnasMeter.RefreshReuseDetected.Add(1,
                new KeyValuePair<string, object?>("family.revoked", true));
            _logger.LogWarning(
                "Refresh-token reuse detected: family={FamilyId} userId={UserId}. Family revoked.",
                existing.FamilyId, existing.UserId);
            return Result<RefreshTokenIssueResult>.Failure(
                ErrorCodes.RefreshTokenReused,
                "Refresh token has already been consumed — family revoked.");
        }

        // 4. Account-state gate. A suspended/disabled/locked account must not be able
        //    to mint fresh access tokens via refresh; we also revoke the family as a
        //    safety measure so the next call cannot retry.
        var user = await _db.UserProfiles
            .FirstOrDefaultAsync(u => u.Id == existing.UserId, ct)
            .ConfigureAwait(false);
        if (user is null || user.State != UserAccountState.Active)
        {
            await RevokeFamilyRowsAsync(existing.FamilyId, "account-not-active", now, ct).ConfigureAwait(false);
            return Result<RefreshTokenIssueResult>.Failure(
                ErrorCodes.RefreshTokenRevoked,
                "The underlying user account is no longer active; refresh-token family revoked.");
        }

        // 5. Happy path — consume the presented token, mint a child, persist both.
        existing.ConsumedAtUtc = now;
        existing.UpdatedAtUtc = now;

        var (newPlaintext, newHash) = GenerateTokenAndHash();
        var newExpires = now + _options.RefreshTokenLifetime;
        var child = new RefreshToken
        {
            TokenHash = newHash,
            FamilyId = existing.FamilyId,
            ParentTokenId = existing.Id,
            UserId = existing.UserId,
            IssuedAtUtc = now,
            ExpiresAtUtc = newExpires,
            CreatedAtUtc = now,
            IsActive = true,
        };
        _db.RefreshTokens.Add(child);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // R0040 — successful rotation. Counted AFTER persistence so a SaveChanges
        // throw above propagates without inflating the success counter.
        CnasMeter.RefreshRotated.Add(1);

        return Result<RefreshTokenIssueResult>.Success(
            new RefreshTokenIssueResult(newPlaintext, existing.FamilyId, newExpires, existing.UserId));
    }

    /// <inheritdoc />
    public async Task<Result> RevokeFamilyAsync(string opaqueRefreshToken, string reason, CancellationToken ct = default)
    {
        // Empty / whitespace tokens are silently treated as a no-op success — logout is
        // idempotent and we do not want clients to be able to probe token existence by
        // observing differing responses.
        if (string.IsNullOrWhiteSpace(opaqueRefreshToken))
        {
            return Result.Success();
        }

        var hash = Sha256Hex(opaqueRefreshToken);
        var existing = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return Result.Success();
        }

        await RevokeFamilyRowsAsync(existing.FamilyId, reason ?? "logout", _clock.UtcNow, ct)
            .ConfigureAwait(false);
        // R0040 — explicit family revoke (logout). Distinct from the reuse-detection
        // counter above so dashboards can tell normal logout traffic apart from the
        // security-critical reuse signal.
        CnasMeter.RefreshRevoked.Add(1);
        return Result.Success();
    }

    /// <summary>
    /// Flips <see cref="RefreshToken.RevokedAtUtc"/> + <see cref="RefreshToken.RevokedReason"/>
    /// on every live (non-revoked) row sharing the given family id. Persists changes
    /// in a single SaveChanges call.
    /// </summary>
    /// <param name="familyId">Family GUID whose live rows should be revoked.</param>
    /// <param name="reason">Stable reason captured on each revoked row.</param>
    /// <param name="now">Revocation timestamp; sourced from the clock once per call.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RevokeFamilyRowsAsync(Guid familyId, string reason, DateTime now, CancellationToken ct)
    {
        var live = await _db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (live.Count == 0)
        {
            return;
        }
        foreach (var row in live)
        {
            row.RevokedAtUtc = now;
            row.RevokedReason = reason;
            row.UpdatedAtUtc = now;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates a fresh opaque refresh token (48 random bytes, base64url-encoded
    /// without padding) and its SHA-256 hex digest. The plaintext is returned to the
    /// caller exactly once; only the digest goes to the database.
    /// </summary>
    /// <returns>Tuple of (plaintext, hash).</returns>
    private static (string Plaintext, string Hash) GenerateTokenAndHash()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        var plaintext = Base64UrlEncode(bytes);
        var hash = Sha256Hex(plaintext);
        return (plaintext, hash);
    }

    /// <summary>
    /// Computes the lowercase-hex SHA-256 digest of the given UTF-8 string. Used both
    /// to mint new hashes at issue time and to look existing rows up at rotate/revoke
    /// time — the column index is fed by the same canonical form on both paths.
    /// </summary>
    /// <param name="value">UTF-8 string to digest.</param>
    /// <returns>64-char lowercase hex digest.</returns>
    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Base64url (RFC 4648 §5) encoding without padding — URL-safe so the token can
    /// travel in a cookie / header / JSON body without further escaping.
    /// </summary>
    /// <param name="bytes">Raw bytes to encode.</param>
    /// <returns>Padding-free base64url string.</returns>
    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
