using System.Globalization;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
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
/// R1702-R1708 / TOR CF 14.12 / Annex 4 — integration tests for the second
/// batch of <see cref="InteropService"/> operations. Exercises each new op
/// on happy + not-found paths and asserts the per-op audit + counter
/// emission. Tests follow the harness pattern established by the R0634
/// service tests.
/// </summary>
public sealed class AnnexFourInteropServiceTests
{
    /// <summary>Fixed clock instant used by every audit-row timestamp.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-annex4-{Guid.NewGuid():N}")
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

    /// <summary>Canonical valid IDNP shared across happy paths.</summary>
    private static readonly string ValidIdnp = BuildIdnp("200012345678");

    /// <summary>
    /// Builds a syntactically valid IDNO from a 12-digit prefix by computing
    /// the same mod-10 weighted checksum the IDNO value-object expects
    /// (weights 7, 3, 1 cycling, mod 10).
    /// </summary>
    private static string BuildIdno(string twelveDigitPrefix)
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

    /// <summary>Canonical valid IDNO shared across happy paths (13 digits, first non-zero, checksum-valid).</summary>
    private static readonly string ValidIdno = BuildIdno("100355012345");

    /// <summary>Stub clock returning <see cref="ClockNow"/>.</summary>
    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    /// <summary>Cached role array shared by the caller mock.</summary>
    private static readonly string[] InteropRoles = new[] { "InteropClient" };

    /// <summary>Builds an audit-capturing mock returning success.</summary>
    private static (IAuditService Audit, Func<string?> LastEvent) NewAuditCapture()
    {
        string? lastEvent = null;
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Do<string>(e => lastEvent = e),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        return (audit, () => lastEvent);
    }

    /// <summary>Builds a Sqid stub mirroring the production "SQID-{id}" shape.</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Builds an interop-caller context substitute.</summary>
    private static ICallerContext NewInteropCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns((string?)null);
        caller.UserId.Returns((long?)null);
        caller.SourceIp.Returns("203.0.113.5");
        caller.CorrelationId.Returns("corr-annex4-1");
        caller.Roles.Returns(InteropRoles);
        return caller;
    }

    /// <summary>Test harness — bundles the service with its read-only DB + audit capture.</summary>
    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required InteropService Service { get; init; }
        public required Func<string?> LastAuditEvent { get; init; }

        public static Harness Create()
        {
            var db = CreateContext();
            var (audit, lastEvent) = NewAuditCapture();
            var caller = NewInteropCaller();
            var sqids = NewSqidMock();
            var svc = new InteropService(db, IdHashHelper.Instance, new StubClock(), sqids, audit, caller);
            return new Harness { Db = db, Service = svc, LastAuditEvent = lastEvent };
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

        public async Task<long> SeedPersonalAccountAsync(long solicitantId)
        {
            var acc = new PersonalAccount
            {
                OwnerSolicitantId = solicitantId,
                AccountCode = "PA-9001",
                LifetimeContributions = 0m,
                LifetimeMonths = 0,
                CreatedAtUtc = ClockNow.AddDays(-30),
                IsActive = true,
            };
            Db.PersonalAccounts.Add(acc);
            await Db.SaveChangesAsync();
            return acc.Id;
        }

        public async Task SeedAccountEntryAsync(long accountId, int year, int month)
        {
            Db.PersonalAccountEntries.Add(new PersonalAccountEntry
            {
                PersonalAccountId = accountId,
                Year = year,
                Month = month,
                ContributionBaseAmount = 1000m,
                ContributionPaidAmount = 100m,
                SourceCode = "EMPLOYER_REPORT",
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
    // R1702 — GetActiveDecisions
    // ────────────────────────────────────────────────────────────────────

    /// <summary>R1702 — happy path: registered citizen returns an empty decisions list (stub).</summary>
    [Fact]
    public async Task R1702_GetActiveDecisions_KnownIdnp_ReturnsEmptyList()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        var result = await h.Service.GetActiveDecisionsAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Decisions.Should().BeEmpty();
        result.Value.IdnpHashPrefix.Length.Should().Be(InteropService.IdnpHashPrefixLength);
        h.LastAuditEvent().Should().Be(InteropService.AuditActiveDecisions);
    }

    /// <summary>R1702 — unknown IDNP surfaces NOT_FOUND.</summary>
    [Fact]
    public async Task R1702_GetActiveDecisions_UnknownIdnp_ReturnsNotFound()
    {
        var h = Harness.Create();

        var result = await h.Service.GetActiveDecisionsAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>R1702 — malformed IDNP surfaces INVALID_IDNP.</summary>
    [Fact]
    public async Task R1702_GetActiveDecisions_MalformedIdnp_ReturnsInvalidIdnp()
    {
        var h = Harness.Create();

        var result = await h.Service.GetActiveDecisionsAsync("not-an-idnp");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
    }

    // ────────────────────────────────────────────────────────────────────
    // R1703 — GetPaymentStatus
    // ────────────────────────────────────────────────────────────────────

    /// <summary>R1703 — well-formed inputs route through the audit row + return NOT_FOUND (deterministic stub).</summary>
    [Fact]
    public async Task R1703_GetPaymentStatus_ValidSqid_ReturnsNotFound_Audited()
    {
        var h = Harness.Create();

        var result = await h.Service.GetPaymentStatusAsync("SQID-42", new DateOnly(2026, 1, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        h.LastAuditEvent().Should().Be(InteropService.AuditPaymentStatus);
    }

    /// <summary>R1703 — empty Sqid surfaces INVALID_SQID.</summary>
    [Fact]
    public async Task R1703_GetPaymentStatus_EmptySqid_ReturnsInvalidSqid()
    {
        var h = Harness.Create();

        var result = await h.Service.GetPaymentStatusAsync(string.Empty, new DateOnly(2026, 1, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidSqid);
    }

    /// <summary>R1703 — out-of-range year surfaces INVALID_DATE_RANGE.</summary>
    [Fact]
    public async Task R1703_GetPaymentStatus_OutOfRangeYear_ReturnsInvalidDateRange()
    {
        var h = Harness.Create();

        var result = await h.Service.GetPaymentStatusAsync("SQID-42", new DateOnly(1900, 1, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidDateRange);
    }

    // ────────────────────────────────────────────────────────────────────
    // R1704 — GetPayerData
    // ────────────────────────────────────────────────────────────────────

    /// <summary>R1704 — natural-person IDNP probe returns the resolved Solicitant.</summary>
    [Fact]
    public async Task R1704_GetPayerData_NaturalPersonIdnp_ReturnsPayer()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        var result = await h.Service.GetPayerDataAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.PayerKind.Should().Be("NaturalPerson");
        result.Value.DisplayName.Should().Be("Popescu Ion");
        result.Value.CountOfInsuredEmployees.Should().Be(0);
        h.LastAuditEvent().Should().Be(InteropService.AuditPayerData);
    }

    /// <summary>R1704 — legal-entity IDNO probe surfaces NOT_FOUND (deterministic stub).</summary>
    [Fact]
    public async Task R1704_GetPayerData_LegalEntityIdno_ReturnsNotFoundStub()
    {
        var h = Harness.Create();

        var result = await h.Service.GetPayerDataAsync(ValidIdno);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>R1704 — malformed taxpayer code surfaces VALIDATION_FAILED.</summary>
    [Fact]
    public async Task R1704_GetPayerData_MalformedCode_ReturnsValidationFailed()
    {
        var h = Harness.Create();

        var result = await h.Service.GetPayerDataAsync("123ABCXYZ4567");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ────────────────────────────────────────────────────────────────────
    // R1705 — IsBenefitBeneficiary
    // ────────────────────────────────────────────────────────────────────

    /// <summary>R1705 — recent active payment of the probed type returns IsBeneficiary=true.</summary>
    [Fact]
    public async Task R1705_IsBenefitBeneficiary_ActivePayment_ReturnsTrue()
    {
        var h = Harness.Create();
        var sid = await h.SeedSolicitantAsync(ValidIdnp);
        await h.SeedBenefitPaymentAsync(
            sid,
            BenefitType.OldAgePension,
            new DateOnly(ClockNow.Year, ClockNow.Month, 1));

        var result = await h.Service.IsBenefitBeneficiaryAsync(ValidIdnp, "OldAgePension");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsBeneficiary.Should().BeTrue();
        result.Value.Reason.Should().Be(string.Empty);
        h.LastAuditEvent().Should().Be(InteropService.AuditIsBenefitBeneficiary);
    }

    /// <summary>R1705 — no payment rows returns IsBeneficiary=false with a reason code.</summary>
    [Fact]
    public async Task R1705_IsBenefitBeneficiary_NoPayments_ReturnsFalseWithReason()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        var result = await h.Service.IsBenefitBeneficiaryAsync(ValidIdnp, "OldAgePension");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsBeneficiary.Should().BeFalse();
        result.Value.Reason.Should().Be("NO_ACTIVE_DECISION");
    }

    /// <summary>R1705 — unknown citizen returns IsBeneficiary=false with UNKNOWN_IDNP.</summary>
    [Fact]
    public async Task R1705_IsBenefitBeneficiary_UnknownIdnp_ReturnsFalseUnknownIdnp()
    {
        var h = Harness.Create();

        var result = await h.Service.IsBenefitBeneficiaryAsync(ValidIdnp, "OldAgePension");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsBeneficiary.Should().BeFalse();
        result.Value.Reason.Should().Be("UNKNOWN_IDNP");
    }

    /// <summary>R1705 — unknown BenefitType surfaces VALIDATION_FAILED.</summary>
    [Fact]
    public async Task R1705_IsBenefitBeneficiary_UnknownBenefitType_ReturnsValidationFailed()
    {
        var h = Harness.Create();

        var result = await h.Service.IsBenefitBeneficiaryAsync(ValidIdnp, "WhatPension");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ────────────────────────────────────────────────────────────────────
    // R1706 — GetContributionPaymentInfo
    // ────────────────────────────────────────────────────────────────────

    /// <summary>R1706 — well-formed IDNO + period returns NOT_FOUND (deterministic stub).</summary>
    [Fact]
    public async Task R1706_GetContributionPaymentInfo_ValidIdno_ReturnsNotFoundStub_Audited()
    {
        var h = Harness.Create();

        var result = await h.Service.GetContributionPaymentInfoAsync(ValidIdno, new DateOnly(2026, 1, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        h.LastAuditEvent().Should().Be(InteropService.AuditContributionPaymentInfo);
    }

    /// <summary>R1706 — malformed IDNO surfaces INVALID_IDNO.</summary>
    [Fact]
    public async Task R1706_GetContributionPaymentInfo_MalformedIdno_ReturnsInvalidIdno()
    {
        var h = Harness.Create();

        var result = await h.Service.GetContributionPaymentInfoAsync("0000000000000", new DateOnly(2026, 1, 1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
    }

    // ────────────────────────────────────────────────────────────────────
    // R1707 — GetLegalApplicableForm
    // ────────────────────────────────────────────────────────────────────

    /// <summary>R1707 — well-formed envelope returns the NotApplicable branch + parses the host-country prefix.</summary>
    [Fact]
    public async Task R1707_GetLegalApplicableForm_ValidEnvelope_ReturnsNotApplicable_WithHostCode()
    {
        var h = Harness.Create();

        var result = await h.Service.GetLegalApplicableFormAsync(ValidIdnp, "RO_MD_2006");

        result.IsSuccess.Should().BeTrue();
        result.Value.ApplicableForm.Should().Be("NotApplicable");
        result.Value.HostCountryCode.Should().Be("RO");
        h.LastAuditEvent().Should().Be(InteropService.AuditLegalApplicableForm);
    }

    /// <summary>R1707 — malformed agreement code surfaces VALIDATION_FAILED.</summary>
    [Fact]
    public async Task R1707_GetLegalApplicableForm_BadAgreementCode_ReturnsValidationFailed()
    {
        var h = Harness.Create();

        var result = await h.Service.GetLegalApplicableFormAsync(ValidIdnp, "BAD-CODE");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ────────────────────────────────────────────────────────────────────
    // R1708 — GetWorkInsurancePeriod
    // ────────────────────────────────────────────────────────────────────

    /// <summary>R1708 — citizen with no personal account returns zero-totals success.</summary>
    [Fact]
    public async Task R1708_GetWorkInsurancePeriod_NoAccount_ReturnsZeroTotals()
    {
        var h = Harness.Create();
        await h.SeedSolicitantAsync(ValidIdnp);

        var result = await h.Service.GetWorkInsurancePeriodAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalMonths.Should().Be(0);
        result.Value.FirstInsuredMonth.Should().BeNull();
        result.Value.LastInsuredMonth.Should().BeNull();
        result.Value.PeriodCount.Should().Be(0);
        result.Value.CurrentlyInsured.Should().BeFalse();
        h.LastAuditEvent().Should().Be(InteropService.AuditWorkInsurancePeriod);
    }

    /// <summary>R1708 — citizen with three contiguous + one isolated month returns 4 months, 2 periods.</summary>
    [Fact]
    public async Task R1708_GetWorkInsurancePeriod_ContiguousPlusIsolated_ReturnsCorrectCounts()
    {
        var h = Harness.Create();
        var sid = await h.SeedSolicitantAsync(ValidIdnp);
        var acc = await h.SeedPersonalAccountAsync(sid);
        // Contiguous spell: 2024-Jan, Feb, Mar
        await h.SeedAccountEntryAsync(acc, 2024, 1);
        await h.SeedAccountEntryAsync(acc, 2024, 2);
        await h.SeedAccountEntryAsync(acc, 2024, 3);
        // Isolated month: 2025-Jun
        await h.SeedAccountEntryAsync(acc, 2025, 6);

        var result = await h.Service.GetWorkInsurancePeriodAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalMonths.Should().Be(4);
        result.Value.PeriodCount.Should().Be(2);
        result.Value.FirstInsuredMonth.Should().Be(new DateOnly(2024, 1, 1));
        result.Value.LastInsuredMonth.Should().Be(new DateOnly(2025, 6, 1));
        result.Value.CurrentlyInsured.Should().BeFalse();
    }

    /// <summary>R1708 — unknown citizen surfaces NOT_FOUND.</summary>
    [Fact]
    public async Task R1708_GetWorkInsurancePeriod_UnknownIdnp_ReturnsNotFound()
    {
        var h = Harness.Create();

        var result = await h.Service.GetWorkInsurancePeriodAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
