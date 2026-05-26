using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0183 / SEC 043 — unit tests for <see cref="AuditDiffComputer"/>. Exercises the
/// per-shape equality rules, the suppression redaction path, the creation /
/// deletion null-snapshot semantics, and the <see cref="AuditFieldPolicyView.RequireAnyChange"/>
/// short-circuit.
/// </summary>
public class AuditDiffComputerTests
{
    /// <summary>Fake entity exercising the supported value shapes.</summary>
    private sealed class FakeEntity
    {
        /// <summary>Internal primary key — surfaces as the Sqid-encoded EntityId.</summary>
        public long Id { get; set; }

        /// <summary>Tracked string field used in the trivial-change scenarios.</summary>
        public string? DisplayName { get; set; }

        /// <summary>Non-tracked field used to verify NOOPs skip emission.</summary>
        public string? Notes { get; set; }

        /// <summary>Tracked sensitive field used in the suppression scenario.</summary>
        public string? NationalId { get; set; }

        /// <summary>Tracked list field used in the collection-shape scenario.</summary>
        public List<string> Roles { get; set; } = new();

        /// <summary>UTC timestamp used in the DateTime-equality scenario.</summary>
        public DateTime UpdatedAtUtc { get; set; }
    }

    /// <summary>Builds a Sqid-stub that round-trips raw long ids via the <c>"SQID-{n}"</c> prefix.</summary>
    private static ISqidService BuildSqids()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var arg = call.Arg<string?>();
            if (!string.IsNullOrEmpty(arg)
                && arg.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(arg.AsSpan(5), out var n))
            {
                return Result<long>.Success(n);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>Builds a policy view limited to the supplied tracked fields.</summary>
    private static AuditFieldPolicyView View(
        IEnumerable<string> tracked,
        IEnumerable<string>? suppressed = null,
        bool requireAnyChange = true,
        AuditSeverity severity = AuditSeverity.Notice)
        => new(
            EntityType: nameof(FakeEntity),
            TrackedFields: new HashSet<string>(tracked, StringComparer.Ordinal),
            SuppressedFields: new HashSet<string>(suppressed ?? Array.Empty<string>(), StringComparer.Ordinal),
            RequireAnyChange: requireAnyChange,
            Severity: severity);

    [Fact]
    public void Trivial_TrackedChange_EmitsSingleEntry()
    {
        var computer = new AuditDiffComputer(BuildSqids());
        var before = new FakeEntity { Id = 42, DisplayName = "Old", Notes = "same" };
        var after = new FakeEntity { Id = 42, DisplayName = "New", Notes = "same" };
        var policy = View(tracked: new[] { nameof(FakeEntity.DisplayName), nameof(FakeEntity.Notes) });

        var diff = computer.Compute(nameof(FakeEntity), before, after, policy);

        diff.Should().NotBeNull();
        diff!.EntityType.Should().Be(nameof(FakeEntity));
        diff.EntityId.Should().Be("SQID-42");
        diff.Entries.Should().HaveCount(1);
        diff.Entries[0].PropertyName.Should().Be(nameof(FakeEntity.DisplayName));
        diff.Entries[0].BeforeJson.Should().Be("\"Old\"");
        diff.Entries[0].AfterJson.Should().Be("\"New\"");
    }

    [Fact]
    public void NonTracked_ChangeOnly_ReturnsNull_WhenRequireAnyChange()
    {
        var computer = new AuditDiffComputer(BuildSqids());
        var before = new FakeEntity { Id = 1, DisplayName = "Same", Notes = "old" };
        var after = new FakeEntity { Id = 1, DisplayName = "Same", Notes = "new" };
        // Only DisplayName is tracked; Notes change must NOT trigger a diff.
        var policy = View(tracked: new[] { nameof(FakeEntity.DisplayName) });

        var diff = computer.Compute(nameof(FakeEntity), before, after, policy);

        diff.Should().BeNull();
    }

    [Fact]
    public void NullBefore_TreatsAsCreation_EveryTrackedFieldHasNullBefore()
    {
        var computer = new AuditDiffComputer(BuildSqids());
        var after = new FakeEntity { Id = 7, DisplayName = "Born" };
        var policy = View(tracked: new[] { nameof(FakeEntity.DisplayName) });

        var diff = computer.Compute(nameof(FakeEntity), before: null, after: after, policy);

        diff.Should().NotBeNull();
        diff!.Entries.Should().ContainSingle();
        diff.Entries[0].BeforeJson.Should().BeNull();
        diff.Entries[0].AfterJson.Should().Be("\"Born\"");
    }

    [Fact]
    public void NullAfter_TreatsAsDeletion_EveryTrackedFieldHasNullAfter()
    {
        var computer = new AuditDiffComputer(BuildSqids());
        var before = new FakeEntity { Id = 9, DisplayName = "Bye" };
        var policy = View(tracked: new[] { nameof(FakeEntity.DisplayName) });

        var diff = computer.Compute(nameof(FakeEntity), before: before, after: null, policy);

        diff.Should().NotBeNull();
        diff!.Entries.Should().ContainSingle();
        diff.Entries[0].BeforeJson.Should().Be("\"Bye\"");
        diff.Entries[0].AfterJson.Should().BeNull();
    }

    [Fact]
    public void SuppressedField_RedactsValueButReportsChange()
    {
        var computer = new AuditDiffComputer(BuildSqids());
        var before = new FakeEntity { Id = 1, NationalId = "2000000000001" };
        var after = new FakeEntity { Id = 1, NationalId = "2000000000002" };
        var policy = View(
            tracked: new[] { nameof(FakeEntity.NationalId) },
            suppressed: new[] { nameof(FakeEntity.NationalId) });

        var diff = computer.Compute(nameof(FakeEntity), before, after, policy);

        diff.Should().NotBeNull();
        diff!.Entries.Should().ContainSingle();
        diff.Entries[0].PropertyName.Should().Be(nameof(FakeEntity.NationalId));
        diff.Entries[0].BeforeJson.Should().Be("\"[redacted]\"");
        diff.Entries[0].AfterJson.Should().Be("\"[redacted]\"");
    }

    [Fact]
    public void CollectionChange_DetectedViaJsonShape()
    {
        var computer = new AuditDiffComputer(BuildSqids());
        var before = new FakeEntity { Id = 1, Roles = new List<string> { "Reader" } };
        var after = new FakeEntity { Id = 1, Roles = new List<string> { "Reader", "Editor" } };
        var policy = View(tracked: new[] { nameof(FakeEntity.Roles) });

        var diff = computer.Compute(nameof(FakeEntity), before, after, policy);

        diff.Should().NotBeNull();
        diff!.Entries.Should().ContainSingle();
        diff.Entries[0].PropertyName.Should().Be(nameof(FakeEntity.Roles));
        diff.Entries[0].BeforeJson.Should().Contain("Reader");
        diff.Entries[0].AfterJson.Should().Contain("Editor");
    }

    [Fact]
    public void DateTime_ProjectsToUtc_DoesNotEmit_WhenLogicallyEqual()
    {
        var computer = new AuditDiffComputer(BuildSqids());
        // Same UTC instant — one expressed as Utc kind, the other as Unspecified.
        var before = new FakeEntity { Id = 1, UpdatedAtUtc = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc) };
        var after = new FakeEntity { Id = 1, UpdatedAtUtc = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Unspecified) };
        var policy = View(tracked: new[] { nameof(FakeEntity.UpdatedAtUtc) });

        var diff = computer.Compute(nameof(FakeEntity), before, after, policy);

        diff.Should().BeNull(); // RequireAnyChange + zero diff entries → null.
    }
}
