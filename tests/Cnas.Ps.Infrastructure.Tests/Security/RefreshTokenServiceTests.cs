using System.Security.Cryptography;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Security;
using Cnas.Ps.Infrastructure.Tests.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Security;

/// <summary>
/// Unit tests for <see cref="RefreshTokenService"/> — the opaque refresh-token issuer /
/// rotator / family-revoker used by the R0053 token pipeline. Tests use EF Core InMemory
/// and the canonical SHA-256 hash to verify the contract:
/// <list type="bullet">
///   <item>The plaintext refresh token is opaque base64url; only its SHA-256 hash is stored.</item>
///   <item>Rotation marks the consumed token, issues a child under the same family.</item>
///   <item>Re-presenting a consumed token detects reuse and revokes the entire family.</item>
///   <item>Expired, revoked, unknown tokens map to the correct stable error codes.</item>
///   <item>Non-Active users have their family revoked at rotation time.</item>
///   <item>Family revoke is idempotent — unknown tokens silently succeed.</item>
/// </list>
/// </summary>
/// <remarks>
/// Per CLAUDE.md RULE 1 these tests are written BEFORE the implementation. Member of
/// <see cref="CnasMeterCollection"/> — Issue / Rotate / Revoke / reuse-detection
/// all emit on the static meter (<c>cnas.refresh.*</c>).
/// </remarks>
[Collection(CnasMeterCollection.Name)]
public sealed class RefreshTokenServiceTests
{
    /// <summary>Deterministic clock anchor.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Test fixture user id (the seeded Active user).</summary>
    private const long UserId = 7L;

    // ─────────────────────── IssueAsync ───────────────────────

    [Fact]
    public async Task IssueAsync_ReturnsOpaqueToken_AndPersistsRow()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.IssueAsync(UserId);

        result.IsSuccess.Should().BeTrue();
        result.Value.OpaqueToken.Should().NotBeNullOrWhiteSpace();
        result.Value.FamilyId.Should().NotBeEmpty();
        result.Value.ExpiresAtUtc.Should().Be(ClockNow.AddDays(30));

        var rows = await harness.Db.RefreshTokens.ToListAsync();
        rows.Should().ContainSingle();
        var row = rows[0];
        row.UserId.Should().Be(UserId);
        row.FamilyId.Should().Be(result.Value.FamilyId);
        row.ParentTokenId.Should().BeNull("the first token in a family has no parent.");
        row.IssuedAtUtc.Should().Be(ClockNow);
        row.ExpiresAtUtc.Should().Be(ClockNow.AddDays(30));
        row.ConsumedAtUtc.Should().BeNull();
        row.RevokedAtUtc.Should().BeNull();
        row.RevokedReason.Should().BeNull();
    }

    [Fact]
    public async Task IssueAsync_StoresHash_NotPlaintext()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Service.IssueAsync(UserId);

        var row = await harness.Db.RefreshTokens.SingleAsync();
        // The stored hash MUST NOT equal the plaintext under any condition.
        row.TokenHash.Should().NotBe(result.Value.OpaqueToken);
        // It MUST equal SHA-256(plaintext) hex.
        row.TokenHash.Should().Be(Sha256Hex(result.Value.OpaqueToken));
        // Exactly 64 chars (SHA-256 hex digest).
        row.TokenHash.Length.Should().Be(64);
    }

    // ─────────────────────── RotateAsync ───────────────────────

    [Fact]
    public async Task RotateAsync_LiveToken_MarksConsumedAndIssuesChild()
    {
        var harness = await Harness.CreateAsync();
        var first = (await harness.Service.IssueAsync(UserId)).Value;

        var rotated = await harness.Service.RotateAsync(first.OpaqueToken);

        rotated.IsSuccess.Should().BeTrue();
        rotated.Value.OpaqueToken.Should().NotBe(first.OpaqueToken);
        rotated.Value.FamilyId.Should().Be(first.FamilyId, "rotation keeps the family alive.");

        // The original row is now Consumed and the child row points back to it via ParentTokenId.
        var rows = await harness.Db.RefreshTokens.OrderBy(r => r.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].ConsumedAtUtc.Should().Be(ClockNow);
        rows[0].RevokedAtUtc.Should().BeNull();
        rows[1].ParentTokenId.Should().Be(rows[0].Id);
        rows[1].FamilyId.Should().Be(first.FamilyId);
        rows[1].ConsumedAtUtc.Should().BeNull();
        rows[1].RevokedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task RotateAsync_AlreadyConsumed_DetectsReuse_AndRevokesFamily()
    {
        // ─── THE HEADLINE TEST ───
        // 1) issue → rotate (consumes #1, issues #2)
        // 2) present #1 again — reuse detected → entire family revoked → RefreshTokenReused.
        var harness = await Harness.CreateAsync();
        var first = (await harness.Service.IssueAsync(UserId)).Value;
        var second = (await harness.Service.RotateAsync(first.OpaqueToken)).Value;

        // Present the consumed first token again — this is the attacker's stolen copy.
        var reuse = await harness.Service.RotateAsync(first.OpaqueToken);

        reuse.IsFailure.Should().BeTrue();
        reuse.ErrorCode.Should().Be(ErrorCodes.RefreshTokenReused);

        // Every row in the family is now revoked with reason="reuse-detected".
        var rows = await harness.Db.RefreshTokens
            .Where(r => r.FamilyId == first.FamilyId)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(r =>
        {
            r.RevokedAtUtc.Should().Be(ClockNow);
            r.RevokedReason.Should().Be("reuse-detected");
        });
    }

    [Fact]
    public async Task RotateAsync_Expired_ReturnsExpired()
    {
        var harness = await Harness.CreateAsync();
        var first = (await harness.Service.IssueAsync(UserId)).Value;

        // Advance the clock past expiry by rebuilding the service.
        var lateHarness = harness.WithClock(ClockNow.AddDays(31));

        var rotated = await lateHarness.Service.RotateAsync(first.OpaqueToken);

        rotated.IsFailure.Should().BeTrue();
        rotated.ErrorCode.Should().Be(ErrorCodes.RefreshTokenExpired);
    }

    [Fact]
    public async Task RotateAsync_Revoked_ReturnsRevoked()
    {
        var harness = await Harness.CreateAsync();
        var first = (await harness.Service.IssueAsync(UserId)).Value;

        // Logout this family then attempt rotation.
        await harness.Service.RevokeFamilyAsync(first.OpaqueToken, "logout");

        var rotated = await harness.Service.RotateAsync(first.OpaqueToken);

        rotated.IsFailure.Should().BeTrue();
        rotated.ErrorCode.Should().Be(ErrorCodes.RefreshTokenRevoked);
    }

    [Fact]
    public async Task RotateAsync_UnknownToken_ReturnsInvalid()
    {
        var harness = await Harness.CreateAsync();

        var rotated = await harness.Service.RotateAsync("not-a-token-we-issued");

        rotated.IsFailure.Should().BeTrue();
        rotated.ErrorCode.Should().Be(ErrorCodes.RefreshTokenInvalid);
    }

    [Theory]
    [InlineData(UserAccountState.Suspended)]
    [InlineData(UserAccountState.Disabled)]
    [InlineData(UserAccountState.Locked)]
    public async Task RotateAsync_NonActiveUser_RevokesFamily_AndReturnsRevoked(UserAccountState state)
    {
        var harness = await Harness.CreateAsync();
        var first = (await harness.Service.IssueAsync(UserId)).Value;

        // Flip the user out of Active state between issue and rotate.
        var user = await harness.Db.UserProfiles.SingleAsync(u => u.Id == UserId);
        user.State = state;
        await harness.Db.SaveChangesAsync();

        var rotated = await harness.Service.RotateAsync(first.OpaqueToken);

        rotated.IsFailure.Should().BeTrue();
        rotated.ErrorCode.Should().Be(ErrorCodes.RefreshTokenRevoked,
            "a non-Active account must not be able to mint new access tokens via refresh.");

        // The family is now revoked as a safety measure.
        var row = await harness.Db.RefreshTokens.SingleAsync(r => r.FamilyId == first.FamilyId);
        row.RevokedAtUtc.Should().Be(ClockNow);
    }

    // ─────────────────────── RevokeFamilyAsync ───────────────────────

    [Fact]
    public async Task RevokeFamilyAsync_KnownToken_RevokesEveryLiveTokenInFamily()
    {
        var harness = await Harness.CreateAsync();
        var first = (await harness.Service.IssueAsync(UserId)).Value;
        var second = (await harness.Service.RotateAsync(first.OpaqueToken)).Value;
        // Issue a SEPARATE family for the same user; it must NOT be touched by the revoke.
        var otherFamily = (await harness.Service.IssueAsync(UserId)).Value;

        var revoke = await harness.Service.RevokeFamilyAsync(second.OpaqueToken, "logout");

        revoke.IsSuccess.Should().BeTrue();

        // Family rows: all revoked with the supplied reason.
        var familyRows = await harness.Db.RefreshTokens
            .Where(r => r.FamilyId == first.FamilyId).ToListAsync();
        familyRows.Should().HaveCount(2);
        familyRows.Should().AllSatisfy(r =>
        {
            r.RevokedAtUtc.Should().Be(ClockNow);
            r.RevokedReason.Should().Be("logout");
        });

        // Sibling family is intact.
        var otherRow = await harness.Db.RefreshTokens
            .SingleAsync(r => r.FamilyId == otherFamily.FamilyId);
        otherRow.RevokedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task RevokeFamilyAsync_UnknownToken_IsIdempotentSuccess()
    {
        var harness = await Harness.CreateAsync();

        var revoke = await harness.Service.RevokeFamilyAsync("totally-bogus", "logout");

        revoke.IsSuccess.Should().BeTrue("logout must be idempotent — unknown tokens are no-ops.");
    }

    // ─────────────────────── Test harness ───────────────────────

    private static string Sha256Hex(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required RefreshTokenService Service { get; init; }
        public required JwtOptions Options { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var db = CreateContext();
            // Seed an Active user so the rotate-time gate has someone to look up.
            db.UserProfiles.Add(new UserProfile
            {
                Id = UserId,
                DisplayName = "Test User",
                State = UserAccountState.Active,
                CreatedAtUtc = ClockNow.AddDays(-100),
                IsActive = true,
            });
            await db.SaveChangesAsync();

            return BuildAround(db, new StubClock(ClockNow));
        }

        public Harness WithClock(DateTime now) => BuildAround(Db, new StubClock(now), Options);

        private static Harness BuildAround(CnasDbContext db, ICnasTimeProvider clock, JwtOptions? existingOptions = null)
        {
            var options = existingOptions ?? new JwtOptions
            {
                Issuer = "https://cnas.test",
                Audience = "cnas-api",
                SigningKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                AccessTokenLifetime = TimeSpan.FromMinutes(15),
                RefreshTokenLifetime = TimeSpan.FromDays(30),
            };
            var service = new RefreshTokenService(
                db,
                clock,
                Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<RefreshTokenService>.Instance);
            return new Harness { Db = db, Service = service, Options = options };
        }

        private static CnasDbContext CreateContext()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-refresh-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new CnasDbContext(opts);
        }
    }
}
