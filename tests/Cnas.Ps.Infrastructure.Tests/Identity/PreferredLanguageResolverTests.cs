using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Identity;

/// <summary>
/// R0211 / TOR UI 003 — tests for <see cref="PreferredLanguageResolver"/>.
/// Covers the happy path, the unknown-user fallback, the empty-preference
/// fallback, and the out-of-allow-list fallback.
/// </summary>
public sealed class PreferredLanguageResolverTests
{
    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    /// <returns>An isolated context.</returns>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-lang-resolver-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Round-trips USR-{id} sqids for predictable test ids.</summary>
    /// <returns>A sqid substitute.</returns>
    private static ISqidService NewSqids()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"USR-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null && s.StartsWith("USR-", StringComparison.Ordinal)
                && long.TryParse(s[4..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    [Fact]
    public async Task Resolve_KnownUserWithEnglishPreference_ReturnsEn()
    {
        using var db = CreateContext();
        db.UserProfiles.Add(new UserProfile
        {
            DisplayName = "EN User",
            PreferredLanguage = "en",
            IsActive = true,
            CreatedAtUtc = new DateTime(2026, 5, 24, 8, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        var userId = db.UserProfiles.Single().Id;

        var sut = new PreferredLanguageResolver(db, NewSqids());

        var result = await sut.ResolveAsync($"USR-{userId}");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("en");
    }

    [Fact]
    public async Task Resolve_UnknownUser_FallsBackToRo()
    {
        using var db = CreateContext();

        var sut = new PreferredLanguageResolver(db, NewSqids());

        var result = await sut.ResolveAsync("USR-9999");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(PreferredLanguageResolver.DefaultLanguage);
    }

    [Fact]
    public async Task Resolve_NullSqid_FallsBackToRo()
    {
        using var db = CreateContext();

        var sut = new PreferredLanguageResolver(db, NewSqids());

        var result = await sut.ResolveAsync(null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ro");
    }

    [Fact]
    public async Task Resolve_ProfileWithEmptyPreference_FallsBackToRo()
    {
        using var db = CreateContext();
        db.UserProfiles.Add(new UserProfile
        {
            DisplayName = "Empty pref",
            PreferredLanguage = string.Empty, // Pathological row.
            IsActive = true,
            CreatedAtUtc = new DateTime(2026, 5, 24, 8, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        var userId = db.UserProfiles.Single().Id;

        var sut = new PreferredLanguageResolver(db, NewSqids());

        var result = await sut.ResolveAsync($"USR-{userId}");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ro");
    }

    [Fact]
    public async Task Resolve_LegacyStalePreference_FallsBackToRo()
    {
        // Pathological / legacy data: somehow the column holds a value outside
        // the allow-list. The resolver must not surface it.
        using var db = CreateContext();
        db.UserProfiles.Add(new UserProfile
        {
            DisplayName = "Legacy pref",
            PreferredLanguage = "fr",
            IsActive = true,
            CreatedAtUtc = new DateTime(2026, 5, 24, 8, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        var userId = db.UserProfiles.Single().Id;

        var sut = new PreferredLanguageResolver(db, NewSqids());

        var result = await sut.ResolveAsync($"USR-{userId}");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ro");
    }
}
