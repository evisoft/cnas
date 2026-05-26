using System.Text.Json;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Benefits;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0517 / TOR CF 02.05 — service-level tests for
/// <see cref="BenefitPaymentStatusService"/>. Exercises the current-user
/// identity-link resolution, the default window substitution, the explicit
/// window override, the type filter, the rolling totals (Paid last 12 /
/// Scheduled next 3), the per-row sort order, the permission gate, the
/// empty-list case, the validator contract, the audit Sensitive row
/// contract, and the response DTO shape.
/// </summary>
public sealed class BenefitPaymentStatusServiceTests
{
    /// <summary>Fixed UTC clock used by every test (2026-05-22 12:00 UTC).</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>First-of-current-month anchor derived from <see cref="ClockNow"/>.</summary>
    private static readonly DateOnly FirstOfThisMonth = new(2026, 5, 1);

    /// <summary>Builds a fresh EF Core InMemory context.</summary>
    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-benefit-payment-status-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    /// <summary>Sqid mock — encodes "SQID-{id}".</summary>
    private static ISqidService NewSqidMock()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        return sqids;
    }

    /// <summary>Fixed-instant clock substitute.</summary>
    private static ICnasTimeProvider NewClockMock()
    {
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);
        return clock;
    }

    /// <summary>Audit capture — exposes the most-recent invocation arguments.</summary>
    private static (IAuditService Audit, Func<(string Code, AuditSeverity Severity, string? Details, long? TargetId)?> Last)
        NewAuditCapture()
    {
        (string Code, AuditSeverity Severity, string? Details, long? TargetId)? slot = null;
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                slot = (
                    call.ArgAt<string>(0),
                    call.ArgAt<AuditSeverity>(1),
                    call.ArgAt<string>(5),
                    call.ArgAt<long?>(4));
                return Task.FromResult(Result.Success());
            });
        return (audit, () => slot);
    }

    /// <summary>Authenticated-caller helper.</summary>
    private static ICallerContext NewCaller(long userId, params string[] roles)
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns(userId);
        caller.UserSqid.Returns($"USR-{userId}");
        caller.SourceIp.Returns("203.0.113.7");
        caller.CorrelationId.Returns("corr-benefit-payment");
        caller.Roles.Returns(roles);
        return caller;
    }

    /// <summary>Seeds a UserProfile + Solicitant pair linked via NationalIdHash.</summary>
    private static async Task<(long UserId, long SolicitantId)> SeedUserAndSolicitantAsync(
        CnasDbContext db,
        string idnp = "2000123456789")
    {
        var hash = IdHashHelper.Hash(idnp);
        var user = new UserProfile
        {
            DisplayName = "Maria Ionescu",
            NationalId = idnp,
            NationalIdHash = hash,
            Roles = new List<string> { "cnas-user" },
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        var solicitant = new Solicitant
        {
            NationalId = idnp,
            NationalIdHash = hash,
            DisplayName = "Maria Ionescu",
            Kind = ApplicantKind.NaturalPerson,
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        db.UserProfiles.Add(user);
        db.Solicitants.Add(solicitant);
        await db.SaveChangesAsync();
        return (user.Id, solicitant.Id);
    }

    /// <summary>Seeds a single benefit-payment row with the supplied attributes.</summary>
    private static async Task<long> SeedPaymentAsync(
        CnasDbContext db,
        long solicitantId,
        BenefitType type,
        DateOnly paymentMonth,
        BenefitPaymentStatus status,
        decimal net,
        BenefitPaymentMethod method = BenefitPaymentMethod.BankTransfer,
        string? iban = "MD24AG000000000000000001")
    {
        var row = new BenefitPayment
        {
            BeneficiarySolicitantId = solicitantId,
            BenefitType = type,
            PaymentMonth = paymentMonth,
            GrossAmount = net + 100m,
            NetAmount = net,
            TaxWithheld = 100m,
            Status = status,
            Method = method,
            BankAccountIban = method == BenefitPaymentMethod.BankTransfer ? iban : null,
            PostalOrderNumber = method == BenefitPaymentMethod.PostalOrder ? "PO-1234" : null,
            IssuedDate = status >= BenefitPaymentStatus.Issued ? paymentMonth.AddDays(5) : null,
            PaidDate = status == BenefitPaymentStatus.Paid ? paymentMonth.AddDays(7) : null,
            ReturnedDate = status == BenefitPaymentStatus.Returned ? paymentMonth.AddDays(10) : null,
            ReturnReason = status == BenefitPaymentStatus.Returned ? "Closed account" : null,
            CreatedAtUtc = ClockNow.AddDays(-10),
            IsActive = true,
        };
        db.BenefitPayments.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Builds the SUT around the supplied collaborators.</summary>
    private static BenefitPaymentStatusService NewService(
        CnasDbContext db,
        ICallerContext caller,
        IAuditService audit)
        => new(
            db,
            db,
            new BenefitPaymentStatusQueryDtoValidator(),
            NewSqidMock(),
            caller,
            NewClockMock(),
            audit);

    /// <summary>
    /// R0517 / Test 1 — GetForCurrentUserAsync resolves the caller's own
    /// Solicitant via the existing UserProfile→Solicitant identity link.
    /// </summary>
    [Fact]
    public async Task R0517_GetForCurrentUser_ResolvesSolicitantViaIdentityLink()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);
        await SeedPaymentAsync(
            db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-1),
            BenefitPaymentStatus.Paid, net: 1500m);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync(new BenefitPaymentStatusQueryDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.SolicitantSqid.Should().Be($"SQID-{solicitantId}");
        result.Value.Payments.Should().HaveCount(1);
    }

    /// <summary>
    /// R0517 / Test 2 — default window excludes rows outside last-12 /
    /// next-3 months (anchored at first of current month).
    /// </summary>
    [Fact]
    public async Task R0517_GetForCurrentUser_DefaultWindow_ExcludesOldAndFarFuture()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);

        // Inside default window — should appear.
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-6), BenefitPaymentStatus.Paid, net: 1500m);
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(2), BenefitPaymentStatus.Scheduled, net: 1500m);

        // Outside default window — should be excluded.
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-18), BenefitPaymentStatus.Paid, net: 1500m);
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(6), BenefitPaymentStatus.Scheduled, net: 1500m);

        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync(new BenefitPaymentStatusQueryDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.Payments.Should().HaveCount(2);
        result.Value.Payments.Select(p => p.PaymentMonth).Should().NotContain(FirstOfThisMonth.AddMonths(-18));
        result.Value.Payments.Select(p => p.PaymentMonth).Should().NotContain(FirstOfThisMonth.AddMonths(6));
    }

    /// <summary>
    /// R0517 / Test 3 — caller-supplied <c>FromMonth</c>/<c>ToMonth</c>
    /// overrides the default window.
    /// </summary>
    [Fact]
    public async Task R0517_GetForCurrentUser_HonoursExplicitWindow()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);

        await SeedPaymentAsync(db, solicitantId, BenefitType.ChildAllowance,
            new DateOnly(2024, 6, 1), BenefitPaymentStatus.Paid, net: 800m);
        await SeedPaymentAsync(db, solicitantId, BenefitType.ChildAllowance,
            new DateOnly(2024, 7, 1), BenefitPaymentStatus.Paid, net: 800m);
        // Out of explicit window.
        await SeedPaymentAsync(db, solicitantId, BenefitType.ChildAllowance,
            new DateOnly(2024, 5, 1), BenefitPaymentStatus.Paid, net: 800m);

        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var query = new BenefitPaymentStatusQueryDto(
            FromMonth: new DateOnly(2024, 6, 1),
            ToMonth: new DateOnly(2024, 7, 1));
        var result = await sut.GetForCurrentUserAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value.Payments.Should().HaveCount(2);
        result.Value.Payments.Select(p => p.PaymentMonth).Should().BeEquivalentTo(new[]
        {
            new DateOnly(2024, 6, 1), new DateOnly(2024, 7, 1),
        });
    }

    /// <summary>
    /// R0517 / Test 4 — <c>Type</c> filter narrows the row list to only the
    /// matching benefit type.
    /// </summary>
    [Fact]
    public async Task R0517_GetForCurrentUser_TypeFilter_NarrowsList()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);

        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-2), BenefitPaymentStatus.Paid, net: 2000m);
        await SeedPaymentAsync(db, solicitantId, BenefitType.ChildAllowance,
            FirstOfThisMonth.AddMonths(-2), BenefitPaymentStatus.Paid, net: 800m);

        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var query = new BenefitPaymentStatusQueryDto(Type: "OldAgePension");
        var result = await sut.GetForCurrentUserAsync(query);

        result.IsSuccess.Should().BeTrue();
        result.Value.Payments.Should().HaveCount(1);
        result.Value.Payments[0].BenefitType.Should().Be("OldAgePension");
    }

    /// <summary>
    /// R0517 / Test 5 — <c>TotalPaidLast12Months</c> sums only Paid entries
    /// in the rolling 12-month lookback.
    /// </summary>
    [Fact]
    public async Task R0517_TotalPaidLast12Months_SumsOnlyPaidEntriesInWindow()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);

        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-6), BenefitPaymentStatus.Paid, net: 1500m);
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-2), BenefitPaymentStatus.Paid, net: 1500m);
        // Scheduled, not Paid — excluded.
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-1), BenefitPaymentStatus.Scheduled, net: 9999m);
        // Outside window — excluded.
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-15), BenefitPaymentStatus.Paid, net: 9999m);

        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync(new BenefitPaymentStatusQueryDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalPaidLast12Months.Should().Be(3000m);
    }

    /// <summary>
    /// R0517 / Test 6 — <c>TotalScheduledNext3Months</c> sums only
    /// Scheduled entries in the rolling next-3 window.
    /// </summary>
    [Fact]
    public async Task R0517_TotalScheduledNext3Months_SumsOnlyScheduledEntriesInWindow()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);

        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(1), BenefitPaymentStatus.Scheduled, net: 1500m);
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(2), BenefitPaymentStatus.Scheduled, net: 1500m);
        // Paid (not Scheduled) — excluded from scheduled total.
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(1), BenefitPaymentStatus.Paid, net: 9999m);

        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync(new BenefitPaymentStatusQueryDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalScheduledNext3Months.Should().Be(3000m);
    }

    /// <summary>
    /// R0517 / Test 7 — payments are sorted by <c>PaymentMonth</c> DESC.
    /// </summary>
    [Fact]
    public async Task R0517_Payments_SortedByPaymentMonthDescending()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);

        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-6), BenefitPaymentStatus.Paid, net: 1500m);
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-1), BenefitPaymentStatus.Paid, net: 1500m);
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-3), BenefitPaymentStatus.Paid, net: 1500m);

        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync(new BenefitPaymentStatusQueryDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.Payments.Select(p => p.PaymentMonth).Should().ContainInOrder(
            FirstOfThisMonth.AddMonths(-1),
            FirstOfThisMonth.AddMonths(-3),
            FirstOfThisMonth.AddMonths(-6));
    }

    /// <summary>
    /// R0517 / Test 8 — GetForSolicitantAsync without
    /// <c>BenefitPayment.ReadAny</c> returns Forbidden.
    /// </summary>
    [Fact]
    public async Task R0517_GetForSolicitant_WithoutReadAnyPermission_ReturnsForbidden()
    {
        var db = CreateContext();
        var (_, solicitantId) = await SeedUserAndSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId: 99L, roles: "cnas-user"), audit);

        var result = await sut.GetForSolicitantAsync(solicitantId, new BenefitPaymentStatusQueryDto());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Forbidden);
    }

    /// <summary>
    /// R0517 / Test 9 — admin with <c>BenefitPayment.ReadAny</c> can read
    /// the status for any Solicitant.
    /// </summary>
    [Fact]
    public async Task R0517_GetForSolicitant_WithReadAnyPermission_ReturnsStatus()
    {
        var db = CreateContext();
        var (_, solicitantId) = await SeedUserAndSolicitantAsync(db);
        await SeedPaymentAsync(db, solicitantId, BenefitType.DisabilityPension,
            FirstOfThisMonth.AddMonths(-2), BenefitPaymentStatus.Paid, net: 1800m);
        var (audit, _) = NewAuditCapture();
        var adminCaller = NewCaller(
            userId: 999L,
            roles: BenefitPaymentStatusService.ReadAnyPermission);
        var sut = NewService(db, adminCaller, audit);

        var result = await sut.GetForSolicitantAsync(solicitantId, new BenefitPaymentStatusQueryDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.SolicitantSqid.Should().Be($"SQID-{solicitantId}");
        result.Value.Payments.Should().HaveCount(1);
    }

    /// <summary>
    /// R0517 / Test 10 — no-payments case returns empty list with zero totals.
    /// </summary>
    [Fact]
    public async Task R0517_NoPayments_ReturnsEmptyListWithZeroTotals()
    {
        var db = CreateContext();
        var (userId, _) = await SeedUserAndSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync(new BenefitPaymentStatusQueryDto());

        result.IsSuccess.Should().BeTrue();
        result.Value.Payments.Should().BeEmpty();
        result.Value.TotalPaidLast12Months.Should().Be(0m);
        result.Value.TotalScheduledNext3Months.Should().Be(0m);
    }

    /// <summary>
    /// R0517 / Test 11 — validator rejects FromMonth &gt; ToMonth.
    /// </summary>
    [Fact]
    public async Task R0517_Validator_RejectsFromMonthAfterToMonth()
    {
        var db = CreateContext();
        var (userId, _) = await SeedUserAndSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var query = new BenefitPaymentStatusQueryDto(
            FromMonth: new DateOnly(2026, 6, 1),
            ToMonth: new DateOnly(2026, 1, 1));
        var result = await sut.GetForCurrentUserAsync(query);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// R0517 / Test 12 — validator rejects window &gt; 36 months.
    /// </summary>
    [Fact]
    public async Task R0517_Validator_RejectsWindowOver36Months()
    {
        var db = CreateContext();
        var (userId, _) = await SeedUserAndSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        // 48-month window — exceeds the 36-month cap.
        var query = new BenefitPaymentStatusQueryDto(
            FromMonth: new DateOnly(2022, 1, 1),
            ToMonth: new DateOnly(2025, 12, 1));
        var result = await sut.GetForCurrentUserAsync(query);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// R0517 / Test 13 — audit Sensitive row carries the solicitantSqid +
    /// monthsReturned + totalPaid payload.
    /// </summary>
    [Fact]
    public async Task R0517_AuditRow_IsSensitive_CarriesSolicitantSqidAndCounters()
    {
        var db = CreateContext();
        var (userId, solicitantId) = await SeedUserAndSolicitantAsync(db);
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-2), BenefitPaymentStatus.Paid, net: 1500m);
        await SeedPaymentAsync(db, solicitantId, BenefitType.OldAgePension,
            FirstOfThisMonth.AddMonths(-1), BenefitPaymentStatus.Paid, net: 1500m);
        var (audit, lastAudit) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync(new BenefitPaymentStatusQueryDto());

        result.IsSuccess.Should().BeTrue();
        var captured = lastAudit();
        captured.Should().NotBeNull();
        captured!.Value.Code.Should().Be(BenefitPaymentStatusService.AuditEventCode);
        captured.Value.Severity.Should().Be(AuditSeverity.Sensitive);
        captured.Value.Details.Should().NotBeNull();
        using var doc = JsonDocument.Parse(captured.Value.Details!);
        doc.RootElement.GetProperty("solicitantSqid").GetString().Should().Be($"SQID-{solicitantId}");
        doc.RootElement.GetProperty("monthsReturned").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("totalPaid").GetString().Should().Be("3000.00");
    }

    /// <summary>
    /// R0517 / Test 14 — anonymous caller cannot read self-service status.
    /// </summary>
    [Fact]
    public async Task R0517_GetForCurrentUser_AnonymousCaller_ReturnsUnauthorized()
    {
        var db = CreateContext();
        var (_, _) = await SeedUserAndSolicitantAsync(db);
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns((long?)null);
        caller.Roles.Returns(Array.Empty<string>());
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, caller, audit);

        var result = await sut.GetForCurrentUserAsync(new BenefitPaymentStatusQueryDto());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    /// <summary>
    /// R0517 / Test 15 — caller with no Solicitant link returns NotFound.
    /// </summary>
    [Fact]
    public async Task R0517_GetForCurrentUser_NoSolicitantLink_ReturnsNotFound()
    {
        var db = CreateContext();
        db.UserProfiles.Add(new UserProfile
        {
            DisplayName = "Orphan",
            NationalId = null,
            NationalIdHash = null,
            Roles = new List<string> { "cnas-user" },
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        });
        await db.SaveChangesAsync();
        var userId = db.UserProfiles.Single().Id;
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var result = await sut.GetForCurrentUserAsync(new BenefitPaymentStatusQueryDto());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>
    /// R0517 / Test 16 — validator rejects an unknown Type string.
    /// </summary>
    [Fact]
    public async Task R0517_Validator_RejectsUnknownTypeString()
    {
        var db = CreateContext();
        var (userId, _) = await SeedUserAndSolicitantAsync(db);
        var (audit, _) = NewAuditCapture();
        var sut = NewService(db, NewCaller(userId), audit);

        var query = new BenefitPaymentStatusQueryDto(Type: "NotAValidBenefitType");
        var result = await sut.GetForCurrentUserAsync(query);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }
}
