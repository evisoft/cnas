using System.Globalization;
using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts.Interop;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Interop;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Interop;

/// <summary>
/// R0634 / TOR CF 14.12 / Annex 4 — integration tests for
/// <see cref="InteropService"/>. Exercises all four ops on happy +
/// edge paths; asserts the no-PII discipline (hash-prefix only, never the
/// raw IDNP) and the per-call audit row.
/// </summary>
public sealed class InteropServiceTests
{
    /// <summary>Fixed clock instant used by every audit-row timestamp.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-interop-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>
    /// Builds a syntactically valid IDNP from a 12-digit prefix by computing
    /// the mod-10 weighted checksum (same algorithm as the Idnp value
    /// object).
    /// </summary>
    private static string BuildIdnp(string twelveDigitPrefix)
    {
        int[] weights = { 7, 3, 1 };
        int sum = 0;
        for (int i = 0; i < 12; i++)
        {
            sum += (twelveDigitPrefix[i] - '0') * weights[i % 3];
        }
        int check = (10 - (sum % 10)) % 10;
        return twelveDigitPrefix + check.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Canonical valid IDNP shared by happy-path tests.</summary>
    private static readonly string ValidIdnp = BuildIdnp("200012345678");

    /// <summary>Second canonical IDNP used by unknown-citizen tests.</summary>
    private static readonly string UnknownIdnp = BuildIdnp("199912345678");

    /// <summary>Stub clock returning <see cref="ClockNow"/>.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    /// <summary>Captures the first detailsJson + eventCode arguments.</summary>
    private static (IAuditService Audit, Func<string?> LastDetails, Func<string?> LastEvent) NewAuditCapture()
    {
        string? lastDetails = null;
        string? lastEvent = null;
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Do<string>(e => lastEvent = e),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Do<string>(s => lastDetails = s),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        return (audit, () => lastDetails, () => lastEvent);
    }

    /// <summary>Builds a Sqid stub that mirrors the production "SQID-{id}" shape.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Cached interop-role array so the NSubstitute setup does not allocate per call.</summary>
    private static readonly string[] InteropRoles = new[] { "InteropClient" };

    /// <summary>Builds an interop-caller context (no UserSqid, role InteropClient).</summary>
    private static ICallerContext NewInteropCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns((string?)null);
        caller.UserId.Returns((long?)null);
        caller.SourceIp.Returns("203.0.113.5");
        caller.CorrelationId.Returns("corr-interop-1");
        caller.Roles.Returns(InteropRoles);
        return caller;
    }

    /// <summary>
    /// Test harness — bundles the service together with its read-only DB,
    /// audit capture, and an accessor for the underlying CnasDbContext for
    /// seed operations.
    /// </summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required InteropService Service { get; init; }
        public required Func<string?> LastAuditDetails { get; init; }
        public required Func<string?> LastAuditEvent { get; init; }
        public required IDeterministicHasher Hasher { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var (audit, lastDetails, lastEvent) = NewAuditCapture();
            var caller = NewInteropCaller();
            var sqids = NewSqidMock();
            var svc = new InteropService(db, IdHashHelper.Instance, new StubClock(), sqids, audit, caller);
            return new Harness
            {
                Db = db,
                Service = svc,
                LastAuditDetails = lastDetails,
                LastAuditEvent = lastEvent,
                Hasher = IdHashHelper.Instance,
            };
        }

        public async Task<long> SeedSolicitantAsync(string idnp, bool isActive = true)
        {
            var entity = new Solicitant
            {
                NationalId = idnp,
                NationalIdHash = IdHashHelper.Hash(idnp),
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Popescu Ion",
                Email = "ion.popescu@example.md",
                PhoneE164 = "+37322000000",
                PreferredLanguage = "ro",
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = isActive,
            };
            Db.Solicitants.Add(entity);
            await Db.SaveChangesAsync();
            return entity.Id;
        }

        public async Task<long> SeedPersonalAccountAsync(
            long solicitantId,
            string accountCode,
            decimal lifetimeContributions,
            int lifetimeMonths)
        {
            var account = new PersonalAccount
            {
                OwnerSolicitantId = solicitantId,
                AccountCode = accountCode,
                LifetimeContributions = lifetimeContributions,
                LifetimeMonths = lifetimeMonths,
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.PersonalAccounts.Add(account);
            await Db.SaveChangesAsync();
            return account.Id;
        }

        public async Task SeedContributionEntryAsync(
            long accountId,
            int year,
            int month,
            decimal @base,
            decimal paid,
            string source)
        {
            Db.PersonalAccountEntries.Add(new PersonalAccountEntry
            {
                PersonalAccountId = accountId,
                Year = year,
                Month = month,
                ContributionBaseAmount = @base,
                ContributionPaidAmount = paid,
                SourceCode = source,
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        public async Task SeedBenefitPaymentAsync(
            long solicitantId,
            BenefitType type,
            DateOnly paymentMonth,
            BenefitPaymentStatus status = BenefitPaymentStatus.Paid)
        {
            Db.BenefitPayments.Add(new BenefitPayment
            {
                BeneficiarySolicitantId = solicitantId,
                BenefitType = type,
                PaymentMonth = paymentMonth,
                GrossAmount = 1000m,
                NetAmount = 950m,
                TaxWithheld = 50m,
                Status = status,
                Method = BenefitPaymentMethod.BankTransfer,
                BankAccountIban = "MD24AG000000022500000000",
                CreatedAtUtc = ClockNow.AddDays(-10),
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // GetInsuredPersonStatus
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Test 1 — happy path: known IDNP → IsRegistered=true, AccountCode populated.</summary>
    [Fact]
    public async Task R0634_GetInsuredPersonStatus_KnownIdnp_ReturnsRegisteredWithAccount()
    {
        var h = Harness.Create();
        var sid = await h.SeedSolicitantAsync(ValidIdnp);
        await h.SeedPersonalAccountAsync(sid, "PA-1001", lifetimeContributions: 5000m, lifetimeMonths: 12);
        await h.SeedBenefitPaymentAsync(sid, BenefitType.OldAgePension, new DateOnly(2026, 5, 1));

        var result = await h.Service.GetInsuredPersonStatusAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsRegistered.Should().BeTrue();
        result.Value.AccountCode.Should().Be("PA-1001");
        result.Value.ActiveBenefitsCount.Should().Be(1);
        result.Value.IdnpHashPrefix.Should().NotBeNullOrWhiteSpace();
        result.Value.IdnpHashPrefix.Length.Should().Be(InteropService.IdnpHashPrefixLength);
        result.Value.AsOfUtc.Should().Be(ClockNow);
    }

    /// <summary>Test 2 — unknown IDNP returns IsRegistered=false but still emits an audit row.</summary>
    [Fact]
    public async Task R0634_GetInsuredPersonStatus_UnknownIdnp_ReturnsNotRegistered_AndAudits()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        var result = await h.Service.GetInsuredPersonStatusAsync(UnknownIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsRegistered.Should().BeFalse();
        result.Value.AccountCode.Should().BeNull();
        result.Value.ActiveBenefitsCount.Should().Be(0);
        h.LastAuditDetails().Should().NotBeNullOrWhiteSpace();
        h.LastAuditEvent().Should().Be(InteropService.AuditInsuredPersonStatus);
    }

    /// <summary>Test 11 — audit row for InsuredPersonStatus uses the correct event code.</summary>
    [Fact]
    public async Task R0634_GetInsuredPersonStatus_WritesAuditWithCorrectEventCode()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        await h.Service.GetInsuredPersonStatusAsync(ValidIdnp);

        h.LastAuditEvent().Should().Be(InteropService.AuditInsuredPersonStatus);
    }

    // ────────────────────────────────────────────────────────────────────
    // GetContributionHistory
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Test 3 — returns months sorted ascending and within the window.</summary>
    [Fact]
    public async Task R0634_GetContributionHistory_KnownIdnp_ReturnsAscendingMonths_WithinWindow()
    {
        var h = Harness.Create();
        var sid = await h.SeedSolicitantAsync(ValidIdnp);
        var acc = await h.SeedPersonalAccountAsync(sid, "PA-2001", 0m, 0);
        // In window
        await h.SeedContributionEntryAsync(acc, 2025, 12, 1000m, 250m, "EMPLOYER_REPORT");
        await h.SeedContributionEntryAsync(acc, 2025, 11, 1000m, 250m, "EMPLOYER_REPORT");
        await h.SeedContributionEntryAsync(acc, 2026, 1, 1000m, 250m, "EMPLOYER_REPORT");
        // Outside window
        await h.SeedContributionEntryAsync(acc, 2023, 1, 1000m, 250m, "EMPLOYER_REPORT");

        var from = new DateOnly(2025, 1, 1);
        var to = new DateOnly(2026, 5, 1);
        var result = await h.Service.GetContributionHistoryAsync(ValidIdnp, from, to);

        result.IsSuccess.Should().BeTrue();
        result.Value.Months.Should().HaveCount(3);
        result.Value.Months.Select(m => (m.Year, m.Month))
            .Should().ContainInOrder(
                (2025, 11),
                (2025, 12),
                (2026, 1));
        result.Value.TotalContributionsInWindow.Should().Be(750m);
        result.Value.MonthsInWindow.Should().Be(3);
    }

    /// <summary>Test 4 — empty window (no matching rows) returns empty list + zero totals.</summary>
    [Fact]
    public async Task R0634_GetContributionHistory_EmptyWindow_ReturnsEmptyMonths()
    {
        var h = Harness.Create();
        var sid = await h.SeedSolicitantAsync(ValidIdnp);
        var acc = await h.SeedPersonalAccountAsync(sid, "PA-3001", 0m, 0);
        await h.SeedContributionEntryAsync(acc, 2020, 1, 1000m, 250m, "EMPLOYER_REPORT");

        var result = await h.Service.GetContributionHistoryAsync(
            ValidIdnp,
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 6, 1));

        result.IsSuccess.Should().BeTrue();
        result.Value.Months.Should().BeEmpty();
        result.Value.TotalContributionsInWindow.Should().Be(0m);
        result.Value.MonthsInWindow.Should().Be(0);
    }

    /// <summary>Test 5 — fromMonth &gt; toMonth surfaces InvalidDateRange.</summary>
    [Fact]
    public async Task R0634_GetContributionHistory_FromAfterTo_ReturnsInvalidDateRange()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        var result = await h.Service.GetContributionHistoryAsync(
            ValidIdnp,
            new DateOnly(2026, 6, 1),
            new DateOnly(2026, 1, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidDateRange);
    }

    /// <summary>Test 6 — window &gt; 60 months surfaces InvalidDateRange.</summary>
    [Fact]
    public async Task R0634_GetContributionHistory_WindowTooLarge_ReturnsInvalidDateRange()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        var result = await h.Service.GetContributionHistoryAsync(
            ValidIdnp,
            new DateOnly(2020, 1, 1),
            new DateOnly(2026, 1, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidDateRange);
    }

    // ────────────────────────────────────────────────────────────────────
    // GetBenefitsList
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Test 7 — groups payments correctly by BenefitType.</summary>
    [Fact]
    public async Task R0634_GetBenefitsList_GroupsByBenefitType()
    {
        var h = Harness.Create();
        var sid = await h.SeedSolicitantAsync(ValidIdnp);
        await h.SeedBenefitPaymentAsync(sid, BenefitType.OldAgePension, new DateOnly(2026, 1, 1));
        await h.SeedBenefitPaymentAsync(sid, BenefitType.OldAgePension, new DateOnly(2026, 2, 1));
        await h.SeedBenefitPaymentAsync(sid, BenefitType.OldAgePension, new DateOnly(2026, 3, 1));
        await h.SeedBenefitPaymentAsync(sid, BenefitType.ChildAllowance, new DateOnly(2024, 6, 1));

        var result = await h.Service.GetBenefitsListAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Benefits.Should().HaveCount(2);

        var oldAge = result.Value.Benefits.Single(b => b.Type == "OldAgePension");
        oldAge.TotalPaymentsCount.Should().Be(3);
        oldAge.FirstPaymentMonth.Should().Be(new DateOnly(2026, 1, 1));
        oldAge.LastPaymentMonth.Should().Be(new DateOnly(2026, 3, 1));

        var child = result.Value.Benefits.Single(b => b.Type == "ChildAllowance");
        child.TotalPaymentsCount.Should().Be(1);
    }

    /// <summary>Unknown citizen surfaces NOT_FOUND on GetBenefitsList.</summary>
    [Fact]
    public async Task R0634_GetBenefitsList_UnknownIdnp_ReturnsNotFound()
    {
        var h = Harness.Create();

        var result = await h.Service.GetBenefitsListAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ────────────────────────────────────────────────────────────────────
    // GetPersonalAccountSnapshot
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Test 8 — returns the cached lifetime totals from PersonalAccount.</summary>
    [Fact]
    public async Task R0634_GetPersonalAccountSnapshot_ReturnsCachedLifetimeTotals()
    {
        var h = Harness.Create();
        var sid = await h.SeedSolicitantAsync(ValidIdnp);
        await h.SeedPersonalAccountAsync(sid, "PA-4001", lifetimeContributions: 123456.78m, lifetimeMonths: 360);

        var result = await h.Service.GetPersonalAccountSnapshotAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccountCode.Should().Be("PA-4001");
        result.Value.LifetimeContributions.Should().Be(123456.78m);
        result.Value.LifetimeMonths.Should().Be(360);
        result.Value.AsOfUtc.Should().Be(ClockNow);
    }

    /// <summary>Citizen exists but no PersonalAccount → NOT_FOUND.</summary>
    [Fact]
    public async Task R0634_GetPersonalAccountSnapshot_NoAccount_ReturnsNotFound()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        var result = await h.Service.GetPersonalAccountSnapshotAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    // ────────────────────────────────────────────────────────────────────
    // Cross-cutting: no-PII discipline + hash-prefix shape + audit
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Test 9 — IdnpHashPrefix is exactly 8 hex chars across all four ops.</summary>
    [Fact]
    public async Task R0634_AllOps_HashPrefixIsExactly8HexChars()
    {
        var h = Harness.Create();
        var sid = await h.SeedSolicitantAsync(ValidIdnp);
        var accId = await h.SeedPersonalAccountAsync(sid, "PA-7001", 100m, 1);
        await h.SeedContributionEntryAsync(accId, 2026, 1, 1000m, 100m, "EMPLOYER_REPORT");
        await h.SeedBenefitPaymentAsync(sid, BenefitType.OldAgePension, new DateOnly(2026, 1, 1));

        var status = await h.Service.GetInsuredPersonStatusAsync(ValidIdnp);
        var history = await h.Service.GetContributionHistoryAsync(
            ValidIdnp,
            new DateOnly(2025, 1, 1),
            new DateOnly(2026, 12, 1));
        var benefits = await h.Service.GetBenefitsListAsync(ValidIdnp);
        var snapshot = await h.Service.GetPersonalAccountSnapshotAsync(ValidIdnp);

        AssertHexPrefix(status.Value.IdnpHashPrefix);
        AssertHexPrefix(history.Value.IdnpHashPrefix);
        AssertHexPrefix(benefits.Value.IdnpHashPrefix);
        AssertHexPrefix(snapshot.Value.IdnpHashPrefix);

        static void AssertHexPrefix(string prefix)
        {
            prefix.Should().NotBeNullOrWhiteSpace();
            prefix.Length.Should().Be(InteropService.IdnpHashPrefixLength);
            prefix.Should().MatchRegex("^[0-9a-f]{8}$");
        }
    }

    /// <summary>Test 10 — none of the four ops echo the raw IDNP back in the response.</summary>
    [Fact]
    public async Task R0634_AllOps_NeverEchoRawIdnpBack()
    {
        var h = Harness.Create();
        var sid = await h.SeedSolicitantAsync(ValidIdnp);
        var accId = await h.SeedPersonalAccountAsync(sid, "PA-8001", 100m, 1);
        await h.SeedContributionEntryAsync(accId, 2026, 1, 1000m, 100m, "EMPLOYER_REPORT");
        await h.SeedBenefitPaymentAsync(sid, BenefitType.OldAgePension, new DateOnly(2026, 1, 1));

        var status = await h.Service.GetInsuredPersonStatusAsync(ValidIdnp);
        var history = await h.Service.GetContributionHistoryAsync(
            ValidIdnp,
            new DateOnly(2025, 1, 1),
            new DateOnly(2026, 12, 1));
        var benefits = await h.Service.GetBenefitsListAsync(ValidIdnp);
        var snapshot = await h.Service.GetPersonalAccountSnapshotAsync(ValidIdnp);

        JsonSerializer.Serialize(status.Value).Should().NotContain(ValidIdnp);
        JsonSerializer.Serialize(history.Value).Should().NotContain(ValidIdnp);
        JsonSerializer.Serialize(benefits.Value).Should().NotContain(ValidIdnp);
        JsonSerializer.Serialize(snapshot.Value).Should().NotContain(ValidIdnp);
    }

    /// <summary>Audit details payload carries the hash prefix, never the raw IDNP.</summary>
    [Fact]
    public async Task R0634_AuditDetails_NeverContainRawIdnp()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        await h.Service.GetInsuredPersonStatusAsync(ValidIdnp);

        var details = h.LastAuditDetails();
        details.Should().NotBeNull();
        details!.Should().NotContain(ValidIdnp);
        details.Should().Contain("idnpHashPrefix");
    }

    // ────────────────────────────────────────────────────────────────────
    // Validator tests (Test 12, 13)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Test 12 — InteropIdnpRequestDtoValidator rejects 12-digit IDNP.</summary>
    [Fact]
    public async Task R0634_IdnpValidator_Rejects12DigitIdnp()
    {
        var validator = new InteropIdnpRequestDtoValidator();
        var dto = new InteropIdnpRequestDto("200012345678");

        var result = await validator.ValidateAsync(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("13", StringComparison.Ordinal));
    }

    /// <summary>Test 13 — InteropIdnpRequestDtoValidator rejects non-digit IDNP.</summary>
    [Fact]
    public async Task R0634_IdnpValidator_RejectsNonDigitIdnp()
    {
        var validator = new InteropIdnpRequestDtoValidator();
        var dto = new InteropIdnpRequestDto("200012345abc7");

        var result = await validator.ValidateAsync(dto);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("digit", StringComparison.Ordinal));
    }

    /// <summary>History validator rejects windows over 60 months.</summary>
    [Fact]
    public async Task R0634_HistoryValidator_RejectsWindowOver60Months()
    {
        var validator = new InteropContributionHistoryRequestValidator();
        var dto = new InteropContributionHistoryRequestDto(
            ValidIdnp,
            new DateOnly(2018, 1, 1),
            new DateOnly(2026, 1, 1));

        var result = await validator.ValidateAsync(dto);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>Service surfaces InvalidIdnp when fed a malformed IDNP.</summary>
    [Fact]
    public async Task R0634_GetInsuredPersonStatus_MalformedIdnp_ReturnsInvalidIdnp()
    {
        var h = Harness.Create();

        var result = await h.Service.GetInsuredPersonStatusAsync("not-an-idnp");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }
}
