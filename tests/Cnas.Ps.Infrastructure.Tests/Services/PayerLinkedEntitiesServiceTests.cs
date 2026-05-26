using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Payers;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Common;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0301 / ARH 028 / TOR Annex 1 — change-traceability of <c>PayerAddress</c>,
/// <c>PayerContact</c>, <c>PayerActivityCAEM</c>, and <c>PayerHistory</c>. These tests
/// land first (CLAUDE.md RULE 1) and exercise the supersession lifecycle, the no-op
/// shortcut, and the audit-emission shape.
/// </summary>
public class PayerLinkedEntitiesServiceTests
{
    /// <summary>Deterministic clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UpdateAddress_PreviousRowExists_FlipsValidToAndInsertsNewRow()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();

        var first = await h.Service.UpdateAddressAsync(
            payerId,
            new PayerAddressInputDto("Str. Stefan 1", "Chisinau", "Chisinau", "MD2001", "MD"),
            "initial", CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await h.Service.UpdateAddressAsync(
            payerId,
            new PayerAddressInputDto("Str. Eminescu 4", "Chisinau", "Chisinau", "MD2009", "MD"),
            "moved", CancellationToken.None);
        second.IsSuccess.Should().BeTrue();

        var rows = await h.Db.PayerAddresses.Where(a => a.PayerId == payerId)
            .OrderBy(a => a.ValidFromUtc).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].ValidToUtc.Should().Be(ClockNow);
        rows[0].Street.Should().Be("Str. Stefan 1");
        rows[1].ValidToUtc.Should().BeNull();
        rows[1].Street.Should().Be("Str. Eminescu 4");
        rows[1].ValidFromUtc.Should().Be(ClockNow);
    }

    [Fact]
    public async Task UpdateAddress_SamePayloadTwice_IsNoOpAndDoesNotWriteSecondRow()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        var input = new PayerAddressInputDto("Str. Stefan 1", "Chisinau", "Chisinau", "MD2001", "MD");
        await h.Service.UpdateAddressAsync(payerId, input, "initial", CancellationToken.None);

        var dup = await h.Service.UpdateAddressAsync(payerId, input, "should-be-noop", CancellationToken.None);

        dup.IsSuccess.Should().BeTrue();
        var rows = await h.Db.PayerAddresses.Where(a => a.PayerId == payerId).ToListAsync();
        rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task AddActivityCaem_FirstAndSecondDistinctCodes_BothCoexistAsCurrent()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();

        var first = await h.Service.AddActivityCaemAsync(
            payerId,
            new PayerActivityCaemInputDto("M.69.10", "Legal activities", true),
            "init", CancellationToken.None);
        var second = await h.Service.AddActivityCaemAsync(
            payerId,
            new PayerActivityCaemInputDto("M.69.20", "Accounting", false),
            "init", CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        var current = await h.Db.PayerActivities
            .Where(a => a.PayerId == payerId && a.ValidToUtc == null).ToListAsync();
        current.Should().HaveCount(2);
    }

    [Fact]
    public async Task EndActivityCaem_ClosesOnlyTheTargetActivity()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        var a = await h.Service.AddActivityCaemAsync(
            payerId, new PayerActivityCaemInputDto("M.69.10", "Legal", true), null, CancellationToken.None);
        var b = await h.Service.AddActivityCaemAsync(
            payerId, new PayerActivityCaemInputDto("M.69.20", "Acct", false), null, CancellationToken.None);
        var aId = h.Sqids.TryDecode(a.Value.Id).Value;
        var bId = h.Sqids.TryDecode(b.Value.Id).Value;

        var end = await h.Service.EndActivityCaemAsync(aId, "ceased", CancellationToken.None);

        end.IsSuccess.Should().BeTrue();
        var rowA = await h.Db.PayerActivities.SingleAsync(x => x.Id == aId);
        var rowB = await h.Db.PayerActivities.SingleAsync(x => x.Id == bId);
        rowA.ValidToUtc.Should().Be(ClockNow);
        rowB.ValidToUtc.Should().BeNull();
    }

    [Fact]
    public async Task ListAddressHistory_ReturnsDescendingByValidFromUtc()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        // Seed three address rows by ratcheting the clock forward each call.
        h.Clock.Advance(TimeSpan.FromHours(0));
        await h.Service.UpdateAddressAsync(payerId,
            new PayerAddressInputDto("S1", "C1", "R1", "MD2001", "MD"), null, CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.Service.UpdateAddressAsync(payerId,
            new PayerAddressInputDto("S2", "C2", "R2", "MD2002", "MD"), null, CancellationToken.None);
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.Service.UpdateAddressAsync(payerId,
            new PayerAddressInputDto("S3", "C3", "R3", "MD2003", "MD"), null, CancellationToken.None);

        var history = await h.Service.ListAddressHistoryAsync(payerId, CancellationToken.None);

        history.IsSuccess.Should().BeTrue();
        history.Value.Should().HaveCount(3);
        history.Value[0].Street.Should().Be("S3");
        history.Value[1].Street.Should().Be("S2");
        history.Value[2].Street.Should().Be("S1");
    }

    [Fact]
    public async Task UpdateAddress_ChangedFields_WritePayerHistoryDiffRowsPerField()
    {
        // R0301 — when the parent has a flat field changed alongside the linked address,
        // a PayerHistory row should land for each changed parent-level field. For this
        // particular test we just verify that the address-supersession path inserts a
        // diff row when the street differs (one field changed).
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        await h.Service.UpdateAddressAsync(payerId,
            new PayerAddressInputDto("S1", "C1", "R1", "MD2001", "MD"), "first", CancellationToken.None);

        await h.Service.UpdateAddressAsync(payerId,
            new PayerAddressInputDto("S2", "C1", "R1", "MD2001", "MD"), "moved-street", CancellationToken.None);

        var diffRows = await h.Db.PayerHistory.Where(x => x.PayerId == payerId).ToListAsync();
        // First call (initial insert) emits no diff rows because there's no predecessor;
        // second call (Street changed only) emits exactly one diff row.
        diffRows.Should().HaveCount(1);
        diffRows[0].FieldName.Should().Be("Street");
        diffRows[0].OldValue.Should().Be("S1");
        diffRows[0].NewValue.Should().Be("S2");
        diffRows[0].ChangeReason.Should().Be("moved-street");
    }

    [Fact]
    public async Task UpdateContact_EmitsSensitiveAuditWithHashedDetailsNoPii()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        h.Audit.Events.Clear();

        await h.Service.UpdateContactAsync(payerId,
            new PayerContactInputDto("+37322000000", "x@example.com", "Andrei P."),
            "initial", CancellationToken.None);

        h.Audit.Events.Should().ContainSingle(e =>
            e.EventCode == "PAYERCONTACT.UPDATED" && e.Severity == AuditSeverity.Sensitive);
        var details = h.Audit.Events.Single().DetailsJson;
        details.Should().NotContain("+37322000000");
        details.Should().NotContain("x@example.com");
        details.Should().NotContain("Andrei");
        details.Should().Contain("toValuesHash");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Mutable stub clock supporting <see cref="Advance"/> between calls.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        private DateTime _now;
        public StubClock(DateTime now) { _now = now; }
        public DateTime UtcNow => _now;
        public void Advance(TimeSpan span) { _now = _now + span; }
    }

    /// <summary>Recording audit stub.</summary>
    private sealed class RecordingAudit : IAuditService
    {
        public List<(string EventCode, AuditSeverity Severity, string DetailsJson)> Events { get; } = new();
        public Task<Result> RecordAsync(string eventCode, AuditSeverity severity, string actorId,
            string? targetEntity, long? targetEntityId, string detailsJson, string? sourceIp,
            string? correlationId, CancellationToken cancellationToken = default)
        {
            Events.Add((eventCode, severity, detailsJson));
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>Stub caller.</summary>
    private sealed class StubCaller : ICallerContext
    {
        public long? UserId => 1;
        public string? UserSqid => "caller-sqid";
        public IReadOnlyCollection<string> Roles { get; } = new[] { "CnasAdmin" };
        public string? SourceIp => "127.0.0.1";
        public string? CorrelationId => "corr-1";
        public string? OnBehalfOfPrincipalIdnp => null;
        public string? DelegationPowerId => null;
        public IAccessScope AccessScope => RolesBasedAccessScope.Unscoped;
        public string? SessionId => null;
    }

    /// <summary>Stub deterministic hasher — emits a stable per-input HMAC look-alike.</summary>
    private sealed class StubHasher : IDeterministicHasher
    {
        public string ComputeHash(string canonicalValue)
        {
            ArgumentNullException.ThrowIfNull(canonicalValue);
            using var hmac = new System.Security.Cryptography.HMACSHA256(
                System.Text.Encoding.UTF8.GetBytes("stub-salt-for-tests"));
            return Convert.ToBase64String(
                hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    canonicalValue.Trim().ToUpperInvariant())));
        }
    }

    /// <summary>Test harness — bundles SUT + supporting collaborators on a fresh InMemory DB.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public required CnasDbContext Db { get; init; }
        public required PayerLinkedEntitiesService Service { get; init; }
        public required ISqidService Sqids { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required StubClock Clock { get; init; }

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-payer-linked-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var clock = new StubClock(ClockNow);
            var sqids = new SqidService(Options.Create(new SqidOptions
            {
                Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
                MinLength = 6,
            }));
            var audit = new RecordingAudit();
            var caller = new StubCaller();
            var hasher = new StubHasher();
            var service = new PayerLinkedEntitiesService(db, clock, sqids, caller, audit, hasher);
            return Task.FromResult(new Harness
            {
                Db = db,
                Service = service,
                Sqids = sqids,
                Audit = audit,
                Clock = clock,
            });
        }

        public long SeedPayer()
        {
            var c = new Contributor
            {
                Idno = "1003600012346",
                IdnoHash = "hash-seed",
                Denumire = "SRL Test",
                RegisteredAtUtc = ClockNow.AddDays(-30),
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.Contributors.Add(c);
            Db.SaveChanges();
            return c.Id;
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync().ConfigureAwait(false);
    }
}
