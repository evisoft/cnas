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
/// R0803 / TOR BP 1.1-D — supersession + audit lifecycle of <see cref="PayerBankAccount"/>
/// and <see cref="PayerSecondaryContact"/>. These tests land first (CLAUDE.md RULE 1)
/// and exercise the primary supersession rule, the duplicate-IBAN guard, the close
/// path, and the sensitive-audit emission shape (IBAN hash prefix only, no plaintext).
/// </summary>
public class PayerLinkedEntitiesBankAccountsTests
{
    /// <summary>Deterministic clock instant used across the suite.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AddBankAccount_IsPrimaryTrue_PersistsRowAndEmitsSensitiveAudit()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        h.Audit.Events.Clear();

        var result = await h.Service.AddBankAccountAsync(
            payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "MD24AG000000022500931776", "Agroindbank", "AGRNMD2X", true, "MDL"),
            "initial", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsPrimary.Should().BeTrue();
        result.Value.Currency.Should().Be("MDL");
        var rows = await h.Db.PayerBankAccounts.Where(b => b.PayerId == payerId).ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].ValidToUtc.Should().BeNull();
        rows[0].IsPrimary.Should().BeTrue();
        rows[0].IbanHash.Should().NotBeNullOrEmpty();
        h.Audit.Events.Should().ContainSingle(e =>
            e.EventCode == "PAYERBANKACCOUNT.ADDED" && e.Severity == AuditSeverity.Sensitive);
    }

    [Fact]
    public async Task AddBankAccount_IsPrimaryTrue_SupersedesExistingPrimary()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();

        var first = await h.Service.AddBankAccountAsync(
            payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "MD24AG000000022500931776", "Agroindbank", "AGRNMD2X", true, "MDL"),
            "initial", CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await h.Service.AddBankAccountAsync(
            payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "MD11VI000002251006700164", "Victoriabank", "VICBMD2X", true, "MDL"),
            "switch-primary", CancellationToken.None);
        second.IsSuccess.Should().BeTrue();

        var rows = await h.Db.PayerBankAccounts.Where(b => b.PayerId == payerId)
            .OrderBy(b => b.ValidFromUtc).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].ValidToUtc.Should().Be(ClockNow); // superseded
        rows[0].IsPrimary.Should().BeTrue(); // historical primary flag preserved
        rows[1].ValidToUtc.Should().BeNull();
        rows[1].IsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task AddBankAccount_IsPrimaryFalse_CoexistsWithExistingPrimary()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();

        await h.Service.AddBankAccountAsync(
            payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "MD24AG000000022500931776", "Agroindbank", "AGRNMD2X", true, "MDL"),
            null, CancellationToken.None);
        var secondary = await h.Service.AddBankAccountAsync(
            payerId,
            new PayerBankAccountInputDto(
                "SRL Test EUR", "MD11VI000002251006700164", "Victoriabank", "VICBMD2X", false, "EUR"),
            null, CancellationToken.None);

        secondary.IsSuccess.Should().BeTrue();
        var current = await h.Db.PayerBankAccounts
            .Where(b => b.PayerId == payerId && b.ValidToUtc == null).ToListAsync();
        current.Should().HaveCount(2);
        current.Count(x => x.IsPrimary).Should().Be(1);
    }

    [Fact]
    public async Task AddBankAccount_MalformedIban_ReturnsValidationFailure()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();

        var result = await h.Service.AddBankAccountAsync(
            payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "NOT-AN-IBAN", "Agroindbank", "AGRNMD2X", true, "MDL"),
            null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIban);
    }

    [Fact]
    public async Task AddBankAccount_DuplicateIbanOnSamePayer_ReturnsValidationFailure()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        await h.Service.AddBankAccountAsync(
            payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "MD24AG000000022500931776", "Agroindbank", "AGRNMD2X", false, "MDL"),
            null, CancellationToken.None);

        var dup = await h.Service.AddBankAccountAsync(
            payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "md24 ag00 0000 0225 0093 1776", "Agroindbank", "AGRNMD2X", false, "MDL"),
            null, CancellationToken.None);

        dup.IsFailure.Should().BeTrue();
        dup.ErrorCode.Should().Be(ErrorCodes.InvalidIban);
    }

    [Fact]
    public async Task CloseBankAccount_SetsValidToUtcAndEmitsSensitiveAudit()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        var added = await h.Service.AddBankAccountAsync(
            payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "MD24AG000000022500931776", "Agroindbank", "AGRNMD2X", true, "MDL"),
            null, CancellationToken.None);
        var rowId = h.Sqids.TryDecode(added.Value.Id).Value;
        h.Audit.Events.Clear();

        var close = await h.Service.CloseBankAccountAsync(rowId, "closed by request", CancellationToken.None);

        close.IsSuccess.Should().BeTrue();
        var row = await h.Db.PayerBankAccounts.SingleAsync(b => b.Id == rowId);
        row.ValidToUtc.Should().Be(ClockNow);
        h.Audit.Events.Should().ContainSingle(e =>
            e.EventCode == "PAYERBANKACCOUNT.CLOSED" && e.Severity == AuditSeverity.Sensitive);
    }

    [Fact]
    public async Task ListCurrentBankAccounts_ReturnsOnlyOpenRowsPrimaryFirst()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();

        await h.Service.AddBankAccountAsync(payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "MD24AG000000022500931776", "Agroindbank", "AGRNMD2X", true, "MDL"),
            null, CancellationToken.None);
        var secondary = await h.Service.AddBankAccountAsync(payerId,
            new PayerBankAccountInputDto(
                "SRL Test EUR", "MD11VI000002251006700164", "Victoriabank", "VICBMD2X", false, "EUR"),
            null, CancellationToken.None);
        var secondaryId = h.Sqids.TryDecode(secondary.Value.Id).Value;
        await h.Service.CloseBankAccountAsync(secondaryId, null, CancellationToken.None);

        var listed = await h.Service.ListCurrentBankAccountsAsync(payerId, CancellationToken.None);

        listed.IsSuccess.Should().BeTrue();
        listed.Value.Should().HaveCount(1);
        listed.Value[0].IsPrimary.Should().BeTrue();
        listed.Value[0].Iban.Should().Be("MD24AG000000022500931776");
    }

    [Fact]
    public async Task AddBankAccount_AuditDetails_ContainsIbanHashPrefixOnlyNoPlaintext()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        h.Audit.Events.Clear();

        await h.Service.AddBankAccountAsync(payerId,
            new PayerBankAccountInputDto(
                "SRL Test", "MD24AG000000022500931776", "Agroindbank", "AGRNMD2X", true, "MDL"),
            "initial", CancellationToken.None);

        var details = h.Audit.Events.Single().DetailsJson;
        details.Should().NotContain("MD24AG000000022500931776");
        details.Should().Contain("ibanHashPrefix");
    }

    [Fact]
    public async Task AddSecondaryContact_PersistsRowAndEmitsSensitiveAudit()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        h.Audit.Events.Clear();

        var result = await h.Service.AddSecondaryContactAsync(payerId,
            new PayerSecondaryContactInputDto("Andrei P.", "Accountant", "+37322000001", "ap@example.com"),
            "onboarding", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var row = await h.Db.PayerSecondaryContacts.SingleAsync(x => x.PayerId == payerId);
        row.ValidToUtc.Should().BeNull();
        row.Role.Should().Be("Accountant");
        h.Audit.Events.Should().ContainSingle(e =>
            e.EventCode == "PAYERSECONDARYCONTACT.ADDED" && e.Severity == AuditSeverity.Sensitive);
    }

    [Fact]
    public async Task CloseSecondaryContact_SetsValidToUtcAndEmitsSensitiveAudit()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        var added = await h.Service.AddSecondaryContactAsync(payerId,
            new PayerSecondaryContactInputDto("Andrei P.", "Accountant", "+37322000001", "ap@example.com"),
            null, CancellationToken.None);
        var rowId = h.Sqids.TryDecode(added.Value.Id).Value;
        h.Audit.Events.Clear();

        var close = await h.Service.CloseSecondaryContactAsync(rowId, "left position", CancellationToken.None);

        close.IsSuccess.Should().BeTrue();
        var row = await h.Db.PayerSecondaryContacts.SingleAsync(x => x.Id == rowId);
        row.ValidToUtc.Should().Be(ClockNow);
        h.Audit.Events.Should().ContainSingle(e =>
            e.EventCode == "PAYERSECONDARYCONTACT.CLOSED" && e.Severity == AuditSeverity.Sensitive);
    }

    [Fact]
    public async Task ListCurrentSecondaryContacts_ReturnsOnlyOpenRows()
    {
        await using var h = await Harness.CreateAsync();
        var payerId = h.SeedPayer();
        var a = await h.Service.AddSecondaryContactAsync(payerId,
            new PayerSecondaryContactInputDto("Andrei P.", "Accountant", null, null),
            null, CancellationToken.None);
        await h.Service.AddSecondaryContactAsync(payerId,
            new PayerSecondaryContactInputDto("Bogdan L.", "Legal", null, null),
            null, CancellationToken.None);
        await h.Service.CloseSecondaryContactAsync(
            h.Sqids.TryDecode(a.Value.Id).Value, null, CancellationToken.None);

        var listed = await h.Service.ListCurrentSecondaryContactsAsync(payerId, CancellationToken.None);

        listed.IsSuccess.Should().BeTrue();
        listed.Value.Should().HaveCount(1);
        listed.Value[0].Role.Should().Be("Legal");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Mutable stub clock.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        private readonly DateTime _now;
        public StubClock(DateTime now) { _now = now; }
        public DateTime UtcNow => _now;
    }

    /// <summary>Recording audit stub — captures (eventCode, severity, detailsJson).</summary>
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

        public static Task<Harness> CreateAsync()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-payer-bank-{Guid.NewGuid():N}")
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
