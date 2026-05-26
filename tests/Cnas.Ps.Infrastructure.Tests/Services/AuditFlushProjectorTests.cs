using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0194 / SEC 047 — unit tests for the SHA-256 row-hash computation hosted on
/// <see cref="AuditFlushProjector"/>. The projector is the single source of truth
/// for hashing — both <see cref="AuditDrainer"/> and
/// <see cref="Cnas.Ps.Infrastructure.Jobs.AuditArchiveReplayJob"/> call into the
/// same helper so the chain cannot drift between the live drainer and the
/// failure-replay path. The tests pin determinism, dependence on the previous
/// hash, the null-vs-empty distinction (a tampered "null → \"\"" swap MUST be
/// detectable), and the genesis anchor used by the very first row.
/// </summary>
public class AuditFlushProjectorTests
{
    private static readonly DateTime FixedEventAt = new(2026, 5, 21, 10, 30, 45, DateTimeKind.Utc);

    /// <summary>
    /// Two invocations with identical inputs MUST produce the same hex digest —
    /// the projector is a pure function, no clocks, no randomness.
    /// </summary>
    [Fact]
    public void ComputeRowHash_DeterministicForSameInput()
    {
        var record = NewRecord();

        var first = AuditFlushProjector.ComputeRowHash(record, "GENESIS");
        var second = AuditFlushProjector.ComputeRowHash(record, "GENESIS");

        first.Should().Be(second);
        first.Should().HaveLength(64, "SHA-256 lower-hex is 64 chars.");
    }

    /// <summary>
    /// Two invocations differing only in <c>prevHash</c> MUST produce different
    /// digests — otherwise the chain provides no tamper evidence beyond a single
    /// row's payload (an attacker could detach + reattach rows).
    /// </summary>
    [Fact]
    public void ComputeRowHash_DifferentPrevHash_ProducesDifferentRowHash()
    {
        var record = NewRecord();

        var a = AuditFlushProjector.ComputeRowHash(record, "GENESIS");
        var b = AuditFlushProjector.ComputeRowHash(record, "0000000000000000000000000000000000000000000000000000000000000001");

        a.Should().NotBe(b);
    }

    /// <summary>
    /// Nullable fields stringify as the literal <c>"null"</c>. Swapping a real
    /// null for an empty string is therefore detectable — without this rule an
    /// attacker who edited <c>SourceIp = NULL</c> to <c>SourceIp = ''</c> would
    /// leave the chain intact.
    /// </summary>
    [Fact]
    public void ComputeRowHash_NullFieldsStringifiedAsLiteralNull()
    {
        var withNull = NewRecord(sourceIp: null);
        var withEmpty = NewRecord(sourceIp: string.Empty);

        var hashWithNull = AuditFlushProjector.ComputeRowHash(withNull, "GENESIS");
        var hashWithEmpty = AuditFlushProjector.ComputeRowHash(withEmpty, "GENESIS");

        hashWithNull.Should().NotBe(hashWithEmpty,
            "null → 'null' literal and '' must hash to different digests so the swap is detectable.");
    }

    /// <summary>
    /// Genesis-anchor regression vector. The hash of a fully-specified record
    /// chained from <c>"GENESIS"</c> MUST match a stable known string — any
    /// change in the canonical-form recipe (field order, separator, encoding,
    /// "null" literal …) will break this test and force the migration to be
    /// updated in lockstep. Keep this value pinned; do not "update to match"
    /// without auditing the chain implications.
    /// </summary>
    [Fact]
    public void ComputeRowHash_GenesisAnchor_ProducesKnownHash()
    {
        var record = new AuditEventRecord(
            EventCode: "USER.LOGIN.SUCCESS",
            Severity: AuditSeverity.Information,
            ActorId: "actor-42",
            TargetEntity: "User",
            TargetEntityId: 42L,
            DetailsJson: "{\"ip\":\"127.0.0.1\"}",
            SourceIp: "127.0.0.1",
            CorrelationId: "corr-abc",
            EventAtUtc: FixedEventAt);

        // Recomputed from the canonical form:
        // "GENESIS|2026-05-21T10:30:45.0000000Z|Information|USER.LOGIN.SUCCESS|actor-42|User|42|127.0.0.1|corr-abc|{\"ip\":\"127.0.0.1\"}"
        // SHA-256 of that UTF-8 string, lower hex.
        const string Expected = "239cc84580a22c0c66855029b2ca00bca7f73a484a1bcc099cc97720165b80b5";

        var hash = AuditFlushProjector.ComputeRowHash(record, "GENESIS");

        hash.Should().Be(Expected,
            "regression vector — any drift in the canonical form will fail this and require a coordinated migration update.");
    }

    private static AuditEventRecord NewRecord(string? sourceIp = "127.0.0.1")
        => new(
            EventCode: "TEST.EVT",
            Severity: AuditSeverity.Information,
            ActorId: "actor",
            TargetEntity: "Entity",
            TargetEntityId: 1L,
            DetailsJson: "{}",
            SourceIp: sourceIp,
            CorrelationId: "corr",
            EventAtUtc: FixedEventAt);
}
