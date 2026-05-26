using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Permissions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0673 / TOR CF 18.12 — unit tests for
/// <see cref="GranularPermissionService"/>. Asserts the assign / revoke /
/// has-permission paths plus the idempotent-assign and unknown-input
/// short-circuits.
/// </summary>
public sealed class GranularPermissionServiceTests
{
    /// <summary>Deterministic UTC instant for clock stubs.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Happy path — admin assigns a triple; the service persists a row and
    /// returns the encoded DTO.
    /// </summary>
    [Fact]
    public async Task AssignAsync_HappyPath_PersistsRow()
    {
        var h = Harness.CreateAdmin();

        var result = await h.Service.AssignAsync(RoleCodes.Decider, "Dossier", PermissionVerbs.View);

        result.IsSuccess.Should().BeTrue();
        result.Value.RoleCode.Should().Be(RoleCodes.Decider);
        result.Value.ResourceType.Should().Be("Dossier");
        result.Value.PermissionVerb.Should().Be(PermissionVerbs.View);
        h.Db.GranularPermissionAssignments.Count().Should().Be(1);
    }

    /// <summary>
    /// HasPermissionAsync returns <c>true</c> when the triple has been
    /// granted.
    /// </summary>
    [Fact]
    public async Task HasPermissionAsync_AfterGrant_ReturnsTrue()
    {
        var h = Harness.CreateAdmin();
        await h.Service.AssignAsync(RoleCodes.Decider, "Dossier", PermissionVerbs.View);

        var probe = await h.Service.HasPermissionAsync(RoleCodes.Decider, "Dossier", PermissionVerbs.View);

        probe.IsSuccess.Should().BeTrue();
        probe.Value.Should().BeTrue();
    }

    /// <summary>
    /// HasPermissionAsync returns <c>false</c> when no row matches the
    /// triple. The probe never trips an error code.
    /// </summary>
    [Fact]
    public async Task HasPermissionAsync_NoMatchingRow_ReturnsFalse()
    {
        var h = Harness.CreateAdmin();

        var probe = await h.Service.HasPermissionAsync(RoleCodes.Decider, "Dossier", PermissionVerbs.View);

        probe.IsSuccess.Should().BeTrue();
        probe.Value.Should().BeFalse();
    }

    /// <summary>
    /// RevokeAsync flips <c>IsActive=false</c> on the row; a follow-up probe
    /// returns false.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_AfterGrant_RemovesPermission()
    {
        var h = Harness.CreateAdmin();
        var assign = await h.Service.AssignAsync(RoleCodes.Decider, "Dossier", PermissionVerbs.View);
        assign.IsSuccess.Should().BeTrue();

        var revoke = await h.Service.RevokeAsync(assign.Value.Id);

        revoke.IsSuccess.Should().BeTrue();
        var probe = await h.Service.HasPermissionAsync(RoleCodes.Decider, "Dossier", PermissionVerbs.View);
        probe.Value.Should().BeFalse();
    }

    /// <summary>
    /// Duplicate assign of the same triple is idempotent — the second call
    /// returns success without emitting a second row.
    /// </summary>
    [Fact]
    public async Task AssignAsync_DuplicateAssign_IsIdempotent()
    {
        var h = Harness.CreateAdmin();

        var first = await h.Service.AssignAsync(RoleCodes.Decider, "Dossier", PermissionVerbs.View);
        var second = await h.Service.AssignAsync(RoleCodes.Decider, "Dossier", PermissionVerbs.View);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        // Ids should match — the second call resolved to the same row.
        second.Value.Id.Should().Be(first.Value.Id);
        h.Db.GranularPermissionAssignments.Count().Should().Be(1);
    }

    /// <summary>
    /// Unknown role / resource / verb triples return <c>false</c> from the
    /// probe (deny-by-default) without tripping a failure code.
    /// </summary>
    [Fact]
    public async Task HasPermissionAsync_UnknownRoleOrVerb_ReturnsFalse()
    {
        var h = Harness.CreateAdmin();

        var probeRole = await h.Service.HasPermissionAsync("not-a-role", "Dossier", PermissionVerbs.View);
        var probeVerb = await h.Service.HasPermissionAsync(RoleCodes.Decider, "Dossier", "NotAVerb");

        probeRole.IsSuccess.Should().BeTrue();
        probeRole.Value.Should().BeFalse();
        probeVerb.IsSuccess.Should().BeTrue();
        probeVerb.Value.Should().BeFalse();
    }

    /// <summary>
    /// Assigning an unknown role code fails fast with
    /// <see cref="ErrorCodes.GranularPermissionUnknownRole"/>.
    /// </summary>
    [Fact]
    public async Task AssignAsync_UnknownRole_Fails()
    {
        var h = Harness.CreateAdmin();

        var result = await h.Service.AssignAsync("not-a-role", "Dossier", PermissionVerbs.View);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.GranularPermissionUnknownRole);
    }

    /// <summary>
    /// Non-admin caller cannot mutate the matrix.
    /// </summary>
    [Fact]
    public async Task AssignAsync_NonAdmin_Forbidden()
    {
        var h = Harness.CreateNonAdmin();

        var result = await h.Service.AssignAsync(RoleCodes.Decider, "Dossier", PermissionVerbs.View);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    // ─────────────────────── Harness ───────────────────────

    /// <summary>Deterministic clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Test bundle for the SUT.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required GranularPermissionService Service { get; init; }
        public required ICallerContext Caller { get; init; }
        public required ISqidService Sqids { get; init; }

        public static Harness Create(IReadOnlyCollection<string> roles)
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-granular-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
            sqids.TryDecode(Arg.Any<string>()).Returns(call =>
            {
                var s = call.Arg<string>();
                if (s is null || !s.StartsWith("SQID-", StringComparison.Ordinal))
                {
                    return Result<long>.Failure(ErrorCodes.InvalidSqid, "Not a SQID-prefixed test sqid.");
                }
                return long.TryParse(s["SQID-".Length..], out var id)
                    ? Result<long>.Success(id)
                    : Result<long>.Failure(ErrorCodes.InvalidSqid, "Not a numeric sqid suffix.");
            });

            var clock = new StubClock(ClockNow);
            var caller = Substitute.For<ICallerContext>();
            caller.UserId.Returns(42L);
            caller.UserSqid.Returns("SQID-42");
            caller.Roles.Returns(roles);

            var audit = Substitute.For<IAuditService>();
            audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Result.Success()));

            IReadOnlyCnasDbContext readDb = db;
            var svc = new GranularPermissionService(db, readDb, sqids, clock, caller, audit);
            return new Harness { Db = db, Service = svc, Caller = caller, Sqids = sqids };
        }

        public static Harness CreateAdmin() => Create(new[] { RoleCodes.Admin });

        public static Harness CreateNonAdmin() => Create(new[] { RoleCodes.User });
    }
}
