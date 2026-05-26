using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="UserDirectoryService"/>. Uses EF Core InMemory for
/// persistence and NSubstitute fakes for <see cref="IAuditService"/> and
/// <see cref="ICnasTimeProvider"/>. Each test reasons about a single branch of
/// <see cref="UserDirectoryService.UpsertOnSignInAsync"/>.
/// </summary>
public class UserDirectoryServiceTests
{
    /// <summary>Deterministic clock instant used across the suite for stable assertions.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Default MPass subject claim used by tests that don't customise it.</summary>
    private const string Sub = "mpass-sub-001";

    [Fact]
    public async Task UpsertOnSignInAsync_NewUser_CreatesProfileWithRoles()
    {
        // Arrange — no existing UserProfile rows.
        var harness = Harness.Create();

        // Act — sign in for the first time. Three CNAS roles arrive after MPass mapping.
        var result = await harness.Service.UpsertOnSignInAsync(
            Sub,
            "Ion Popescu",
            "ion@example.md",
            ["cnas-user", "cnas-decider", "cnas-admin"]);

        // Assert — a fresh row exists with the supplied roles, IsActive=true, audit fired.
        result.IsSuccess.Should().BeTrue();
        var persisted = await harness.Db.UserProfiles.SingleAsync(u => u.MPassSubject == Sub);
        persisted.Id.Should().Be(result.Value);
        persisted.DisplayName.Should().Be("Ion Popescu");
        persisted.Email.Should().Be("ion@example.md");
        persisted.Roles.Should().BeEquivalentTo(["cnas-user", "cnas-decider", "cnas-admin"]);
        persisted.IsActive.Should().BeTrue();
        // R0059 — newly-created profiles start in the Active state (was IsLocked = false).
        persisted.State.Should().Be(UserAccountState.Active);
        persisted.CreatedAtUtc.Should().Be(ClockNow);
    }

    [Fact]
    public async Task UpsertOnSignInAsync_ExistingUser_UpdatesNameEmailRoles()
    {
        // Arrange — seed an existing profile with stale name/email/roles.
        var harness = Harness.Create();
        var existing = new UserProfile
        {
            MPassSubject = Sub,
            DisplayName = "Old Name",
            Email = "old@example.md",
            Roles = ["cnas-user"],
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        harness.Db.UserProfiles.Add(existing);
        await harness.Db.SaveChangesAsync();

        // Act — second sign-in supplies refreshed claims.
        var result = await harness.Service.UpsertOnSignInAsync(
            Sub,
            "New Name",
            "new@example.md",
            ["cnas-admin"]);

        // Assert — the row is mutated in place; row count is still 1.
        result.IsSuccess.Should().BeTrue();
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.MPassSubject == Sub);
        reloaded.Id.Should().Be(existing.Id);
        reloaded.DisplayName.Should().Be("New Name");
        reloaded.Email.Should().Be("new@example.md");
        reloaded.Roles.Should().BeEquivalentTo(["cnas-admin"]);
        reloaded.UpdatedAtUtc.Should().Be(ClockNow);
        (await harness.Db.UserProfiles.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpsertOnSignInAsync_EmptyExternalSub_ReturnsValidationFailed()
    {
        var harness = Harness.Create();

        var result = await harness.Service.UpsertOnSignInAsync(
            externalSub: "",
            displayName: "Ion",
            email: null,
            roles: []);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await harness.Db.UserProfiles.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpsertOnSignInAsync_ProfileLocked_ReturnsForbidden()
    {
        // Arrange — locked profile (R0059 State machine); sign-in must NOT silently
        // re-activate it. The State.Locked entry stands for the previous IsLocked=true
        // boolean shape; the new code rejects any non-Active state at the auth gate.
        var harness = Harness.Create();
        harness.Db.UserProfiles.Add(new UserProfile
        {
            MPassSubject = Sub,
            DisplayName = "Locked User",
            IsActive = true,
            State = UserAccountState.Locked,
            CreatedAtUtc = ClockNow.AddDays(-5),
            Roles = ["cnas-user"],
        });
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.UpsertOnSignInAsync(
            Sub, "Locked User", null, ["cnas-admin"]);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
        var reloaded = await harness.Db.UserProfiles.SingleAsync(u => u.MPassSubject == Sub);
        reloaded.State.Should().Be(UserAccountState.Locked, "the lock must persist across sign-ins");
        reloaded.Roles.Should().BeEquivalentTo(["cnas-user"], "roles must not be mutated on a locked profile");
    }

    [Fact]
    public async Task UpsertOnSignInAsync_AuditsSignInSync()
    {
        var harness = Harness.Create();

        var result = await harness.Service.UpsertOnSignInAsync(
            Sub, "Audit Tester", "audit@example.md", ["cnas-user"]);

        result.IsSuccess.Should().BeTrue();
        await harness.Audit.Received(1).RecordAsync(
            "USER_DIRECTORY.SIGN_IN_SYNC",
            AuditSeverity.Information,
            Sub,
            nameof(UserProfile),
            Arg.Any<long>(),
            Arg.Is<string>(s => s.Contains("\"action\":\"created\"") && s.Contains("\"cnas-user\"")),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertOnSignInAsync_NullEmail_AcceptsAndPersists()
    {
        var harness = Harness.Create();

        var result = await harness.Service.UpsertOnSignInAsync(
            Sub, "No Email", email: null, roles: ["cnas-user"]);

        result.IsSuccess.Should().BeTrue();
        var persisted = await harness.Db.UserProfiles.SingleAsync(u => u.MPassSubject == Sub);
        persisted.Email.Should().BeNull();
        persisted.DisplayName.Should().Be("No Email");
    }

    // ─────────────────────── Test harness ───────────────────────

    /// <summary>Creates a fresh EF Core InMemory context with a unique database name.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-dir-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Stub clock returning a fixed instant for deterministic tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Bundles the SUT and its collaborators so tests stay focused on assertions.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required UserDirectoryService Service { get; init; }
        public required IAuditService Audit { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var clock = new StubClock(ClockNow);
            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                    Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));
            var service = new UserDirectoryService(db, clock, audit);
            return new Harness
            {
                Db = db,
                Service = service,
                Audit = audit,
            };
        }
    }
}
