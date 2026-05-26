using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R1504 / TOR §3.7-E — integration tests for
/// <see cref="PaymentSuspensionService"/>. Uses EF Core InMemory + NSubstitute
/// for the surrounding collaborators.
/// </summary>
public sealed class PaymentSuspensionServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-suspend-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record Harness(
        CnasDbContext Db,
        PaymentSuspensionService Sut,
        IAuditService Audit,
        ISqidService Sqids,
        INotificationTriggerDispatcher Triggers);

    private static Harness Create()
    {
        var db = CreateContext();
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-CALLER");
        caller.UserId.Returns(7L);
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-test");

        var triggers = Substitute.For<INotificationTriggerDispatcher>();
        triggers.DispatchAsync(
                Arg.Any<NotificationTriggerKind>(),
                Arg.Any<NotificationTriggerPayload>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var sut = new PaymentSuspensionService(
            db, sqids, new StubClock(ClockNow), caller, audit,
            NullLogger<PaymentSuspensionService>.Instance, triggers);
        return new Harness(db, sut, audit, sqids, triggers);
    }

    private static async Task<(long DecisionId, string DecisionSqid)> SeedDecisionAsync(
        Harness h, string idnp = "2000123456782")
    {
        var solicitant = new Solicitant
        {
            CreatedAtUtc = ClockNow,
            NationalId = idnp,
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Ion Popescu",
            PreferredLanguage = "ro",
            IsActive = true,
        };
        h.Db.Solicitants.Add(solicitant);

        var passport = new ServicePassport
        {
            CreatedAtUtc = ClockNow,
            Code = "SP-TEST",
            NameRo = "Test",
            DescriptionRo = "Test",
            FormSchemaJson = "{}",
            WorkflowCode = "WF",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsActive = true,
        };
        h.Db.ServicePassports.Add(passport);
        await h.Db.SaveChangesAsync();

        var app = new ServiceApplication
        {
            CreatedAtUtc = ClockNow,
            SolicitantId = solicitant.Id,
            ServicePassportId = passport.Id,
            Status = ApplicationStatus.Approved,
            FormPayloadJson = "{}",
            SnapshotJson = "{}",
            SubmittedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        h.Db.Applications.Add(app);
        await h.Db.SaveChangesAsync();

        var sqid = $"DEC-{app.Id}";
        h.Sqids.TryDecode(sqid).Returns(Result<long>.Success(app.Id));
        return (app.Id, sqid);
    }

    private static async Task<long> SeedPendingOrderAsync(Harness h, string idnp)
    {
        var order = new MPayOrder
        {
            CreatedAtUtc = ClockNow,
            OrderId = $"ORD-{Guid.NewGuid():N}".Substring(0, 16),
            AmountMdl = 2500m,
            DescriptionRo = "Plată prestație",
            BeneficiaryIdnp = idnp,
            IsActive = true,
        };
        h.Db.MPayOrders.Add(order);
        await h.Db.SaveChangesAsync();
        return order.Id;
    }

    /// <summary>Suspend happy path.</summary>
    [Fact]
    public async Task SuspendAsync_HappyPath_PersistsRecord()
    {
        var h = Create();
        var (_, decisionSqid) = await SeedDecisionAsync(h);

        var result = await h.Sut.SuspendAsync(decisionSqid, "Certificat medical expirat.");

        result.IsSuccess.Should().BeTrue();
        result.Value.SuspendedAtUtc.Should().Be(ClockNow);
        result.Value.ResumedAtUtc.Should().BeNull();
        (await h.Db.PaymentSuspensionRecords.CountAsync()).Should().Be(1);
        (await h.Db.Documents.CountAsync()).Should().Be(1);
    }

    /// <summary>Suspend then resume happy path.</summary>
    [Fact]
    public async Task ResumeAsync_HappyPath_StampsRecord()
    {
        var h = Create();
        var (_, decisionSqid) = await SeedDecisionAsync(h);
        var sus = await h.Sut.SuspendAsync(decisionSqid, "First reason");
        sus.IsSuccess.Should().BeTrue();
        var suspensionSqid = sus.Value.Sqid;
        h.Sqids.TryDecode(suspensionSqid)
            .Returns(Result<long>.Success(long.Parse(suspensionSqid["SQID-".Length..])));

        var result = await h.Sut.ResumeAsync(suspensionSqid, "Cleared medical proof.");

        result.IsSuccess.Should().BeTrue();
        result.Value.ResumedAtUtc.Should().Be(ClockNow);
        result.Value.ResumeReason.Should().Be("Cleared medical proof.");
        (await h.Db.Documents.CountAsync()).Should().Be(2);
    }

    /// <summary>Double-suspend is rejected with Conflict.</summary>
    [Fact]
    public async Task SuspendAsync_DoubleSuspend_ReturnsConflict()
    {
        var h = Create();
        var (_, decisionSqid) = await SeedDecisionAsync(h);
        var first = await h.Sut.SuspendAsync(decisionSqid, "First reason");
        first.IsSuccess.Should().BeTrue();

        var second = await h.Sut.SuspendAsync(decisionSqid, "Second reason");

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>Resume already-resumed suspension is rejected with Conflict.</summary>
    [Fact]
    public async Task ResumeAsync_AlreadyResumed_ReturnsConflict()
    {
        var h = Create();
        var (_, decisionSqid) = await SeedDecisionAsync(h);
        var sus = await h.Sut.SuspendAsync(decisionSqid, "First reason");
        sus.IsSuccess.Should().BeTrue();
        var suspensionSqid = sus.Value.Sqid;
        var suspensionId = long.Parse(suspensionSqid["SQID-".Length..]);
        h.Sqids.TryDecode(suspensionSqid).Returns(Result<long>.Success(suspensionId));

        var firstResume = await h.Sut.ResumeAsync(suspensionSqid, "Cleared once");
        firstResume.IsSuccess.Should().BeTrue();

        var secondResume = await h.Sut.ResumeAsync(suspensionSqid, "Cleared twice");

        secondResume.IsFailure.Should().BeTrue();
        secondResume.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    /// <summary>Suspend flips related MPayOrder rows to IsSuspended = true.</summary>
    [Fact]
    public async Task SuspendAsync_FlipsRelatedOrdersToSuspended()
    {
        var h = Create();
        var idnp = "2010987654321";
        var (_, decisionSqid) = await SeedDecisionAsync(h, idnp);
        var orderId = await SeedPendingOrderAsync(h, idnp);

        var result = await h.Sut.SuspendAsync(decisionSqid, "Reason for suspension.");

        result.IsSuccess.Should().BeTrue();
        var order = await h.Db.MPayOrders.SingleAsync(o => o.Id == orderId);
        order.IsSuspended.Should().BeTrue();
        order.SuspendedAtUtc.Should().Be(ClockNow);
    }

    /// <summary>Resume flips suspended MPayOrder rows back to active.</summary>
    [Fact]
    public async Task ResumeAsync_FlipsRelatedOrdersBackToActive()
    {
        var h = Create();
        var idnp = "2010987654322";
        var (_, decisionSqid) = await SeedDecisionAsync(h, idnp);
        var orderId = await SeedPendingOrderAsync(h, idnp);
        var sus = await h.Sut.SuspendAsync(decisionSqid, "Reason for suspension.");
        sus.IsSuccess.Should().BeTrue();

        var suspensionSqid = sus.Value.Sqid;
        h.Sqids.TryDecode(suspensionSqid)
            .Returns(Result<long>.Success(long.Parse(suspensionSqid["SQID-".Length..])));

        var resume = await h.Sut.ResumeAsync(suspensionSqid, "Cleared the issue.");

        resume.IsSuccess.Should().BeTrue();
        var order = await h.Db.MPayOrders.SingleAsync(o => o.Id == orderId);
        order.IsSuspended.Should().BeFalse();
    }

    /// <summary>Suspend emits the Critical audit row + ActionResult notification.</summary>
    [Fact]
    public async Task SuspendAsync_EmitsAuditAndNotification()
    {
        var h = Create();
        var (_, decisionSqid) = await SeedDecisionAsync(h);

        var result = await h.Sut.SuspendAsync(decisionSqid, "Reason for suspension.");

        result.IsSuccess.Should().BeTrue();
        await h.Audit.Received(1).RecordAsync(
            PaymentSuspensionService.AuditSuspended,
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(PaymentSuspensionRecord),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await h.Triggers.Received(1).DispatchAsync(
            NotificationTriggerKind.ActionResult,
            Arg.Any<NotificationTriggerPayload>(),
            Arg.Any<CancellationToken>());
    }
}
