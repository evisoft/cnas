using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Users;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0672 / TOR CF 18.08 — unit tests for <see cref="UserDeactivationGuard"/>.
/// Asserts the audit-history check returns success when at least one trail
/// row exists (either AuditLog or EntityHistoryRow) and refuses with
/// <see cref="ErrorCodes.UserProfileNoAuditHistory"/> when neither does.
/// Tests are written BEFORE the production code per CLAUDE.md RULE 1
/// (TDD red → green).
/// </summary>
public sealed class UserDeactivationGuardTests
{
    /// <summary>Deterministic clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Probe-1 negative — no <c>AuditLog</c> nor <c>EntityHistoryRow</c> row
    /// is attributed to the user; the guard MUST refuse with
    /// <see cref="ErrorCodes.UserProfileNoAuditHistory"/>.
    /// </summary>
    [Fact]
    public async Task EnsureCanDeactivateAsync_NoAuditAndNoHistory_Fails()
    {
        await using var db = CreateContext();
        IReadOnlyCnasDbContext readDb = db;
        var guard = new UserDeactivationGuard(readDb);

        // Seed a user with NO audit or history rows whatsoever.
        var user = SeedUser(db);
        await db.SaveChangesAsync();

        var result = await guard.EnsureCanDeactivateAsync(user.Id);

        result.IsFailure.Should().BeTrue(
            "a soft-delete must NEVER land on a user with zero traceability rows.");
        result.ErrorCode.Should().Be(ErrorCodes.UserProfileNoAuditHistory);
    }

    /// <summary>
    /// Probe-2 positive — only an <c>AuditLog</c> row exists (no
    /// <c>EntityHistoryRow</c>). The guard MUST still return success because
    /// either projection is sufficient evidence of traceability.
    /// </summary>
    [Fact]
    public async Task EnsureCanDeactivateAsync_HasAuditOnly_Succeeds()
    {
        await using var db = CreateContext();
        IReadOnlyCnasDbContext readDb = db;
        var guard = new UserDeactivationGuard(readDb);

        var user = SeedUser(db);
        await db.SaveChangesAsync();

        db.AuditLogs.Add(new AuditLog
        {
            EventAtUtc = ClockNow,
            Severity = AuditSeverity.Notice,
            EventCode = "USER.SOME_ACTION",
            ActorId = "SQID-CALLER",
            TargetEntity = nameof(UserProfile),
            TargetEntityId = user.Id,
            PrevHash = "GENESIS",
            RowHash = "deadbeef",
            DetailsJson = "{}",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await guard.EnsureCanDeactivateAsync(user.Id);

        result.IsSuccess.Should().BeTrue(
            "an audit-log row keyed to the user satisfies the audit-history contract.");
    }

    /// <summary>
    /// Probe-1 positive — only an <c>EntityHistoryRow</c> snapshot exists
    /// (no <c>AuditLog</c>). Mirrors the audit-only path; either trail is
    /// enough for the guard to release the soft-delete.
    /// </summary>
    [Fact]
    public async Task EnsureCanDeactivateAsync_HasHistoryOnly_Succeeds()
    {
        await using var db = CreateContext();
        IReadOnlyCnasDbContext readDb = db;
        var guard = new UserDeactivationGuard(readDb);

        var user = SeedUser(db);
        await db.SaveChangesAsync();

        db.EntityHistoryRows.Add(new EntityHistoryRow
        {
            EntityType = nameof(UserProfile),
            EntityId = user.Id,
            ChangedAtUtc = ClockNow,
            Operation = "I",
            PayloadJson = "{}",
            ActorSqid = "SQID-CALLER",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await guard.EnsureCanDeactivateAsync(user.Id);

        result.IsSuccess.Should().BeTrue(
            "an entity-history row keyed to the user satisfies the audit-history contract.");
    }

    /// <summary>
    /// Belt-and-braces positive — BOTH projections carry a row for the user.
    /// The guard MUST return success and SHOULD short-circuit on the first
    /// probe (we don't assert the short-circuit here, only the verdict).
    /// </summary>
    [Fact]
    public async Task EnsureCanDeactivateAsync_HasBoth_Succeeds()
    {
        await using var db = CreateContext();
        IReadOnlyCnasDbContext readDb = db;
        var guard = new UserDeactivationGuard(readDb);

        var user = SeedUser(db);
        await db.SaveChangesAsync();

        db.EntityHistoryRows.Add(new EntityHistoryRow
        {
            EntityType = nameof(UserProfile),
            EntityId = user.Id,
            ChangedAtUtc = ClockNow,
            Operation = "I",
            PayloadJson = "{}",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        db.AuditLogs.Add(new AuditLog
        {
            EventAtUtc = ClockNow,
            Severity = AuditSeverity.Notice,
            EventCode = "USER.SOME_ACTION",
            ActorId = "SQID-CALLER",
            TargetEntity = nameof(UserProfile),
            TargetEntityId = user.Id,
            PrevHash = "GENESIS",
            RowHash = "cafebabe",
            DetailsJson = "{}",
            CreatedAtUtc = ClockNow,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await guard.EnsureCanDeactivateAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
    }

    // ─────────────────────── Test infrastructure ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-userguard-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Adds a stub user to the supplied context. Save responsibility is on
    /// the caller so the test can interleave the user create with the audit /
    /// history rows in a single SaveChangesAsync if it wants to.
    /// </summary>
    /// <param name="db">EF Core context.</param>
    /// <returns>The added (un-saved) profile.</returns>
    private static UserProfile SeedUser(CnasDbContext db)
    {
        var user = new UserProfile
        {
            MPassSubject = $"sub-{Guid.NewGuid():N}",
            DisplayName = "Guard Subject",
            CreatedAtUtc = ClockNow.AddDays(-1),
            IsActive = true,
        };
        db.UserProfiles.Add(user);
        return user;
    }
}
