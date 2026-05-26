using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Notifications;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R1502 / TOR §3.7-C — integration tests for <see cref="DecisionRecomputeService"/>.
/// Uses EF Core InMemory + NSubstitute for the surrounding collaborators.
/// </summary>
public sealed class DecisionRecomputeServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-recompute-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record Harness(
        CnasDbContext Db,
        DecisionRecomputeService Sut,
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
        caller.UserId.Returns(1L);
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-1");

        var triggers = Substitute.For<INotificationTriggerDispatcher>();
        triggers.DispatchAsync(
                Arg.Any<NotificationTriggerKind>(),
                Arg.Any<NotificationTriggerPayload>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var sut = new DecisionRecomputeService(
            db, sqids, new StubClock(ClockNow), caller, audit,
            NullLogger<DecisionRecomputeService>.Instance, triggers);
        return new Harness(db, sut, audit, sqids, triggers);
    }

    private static async Task<long> SeedPriorAsync(Harness h, decimal priorAmount, string sqid = "PRIOR-SQID")
    {
        var solicitant = new Solicitant
        {
            CreatedAtUtc = ClockNow,
            NationalId = "2000123456782",
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
            FormPayloadJson = $"{{\"monthlyAmountMdl\":{priorAmount.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}",
            SnapshotJson = "{}",
            SubmittedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        h.Db.Applications.Add(app);
        await h.Db.SaveChangesAsync();
        h.Sqids.TryDecode(sqid).Returns(Result<long>.Success(app.Id));
        return app.Id;
    }

    /// <summary>Positive delta yields an adjustment document.</summary>
    [Fact]
    public async Task RecomputeAsync_PositiveDelta_EmitsAdjustmentDoc()
    {
        var h = Create();
        await SeedPriorAsync(h, priorAmount: 1000m);

        var result = await h.Sut.RecomputeAsync(
            "PRIOR-SQID", DecisionRecomputeReason.BaseAmountChanged, newMonthlyAmountMdl: 1200m);

        result.IsSuccess.Should().BeTrue();
        result.Value.DocumentKindCode.Should().Be(DecisionRecomputeService.DocCodeAdjustment);
        result.Value.Delta.Should().Be(200m);
        result.Value.NewDocumentSqid.Should().NotBeNull();

        (await h.Db.Documents.CountAsync()).Should().Be(1);
    }

    /// <summary>Negative delta yields a recuperare document.</summary>
    [Fact]
    public async Task RecomputeAsync_NegativeDelta_EmitsRecuperareDoc()
    {
        var h = Create();
        await SeedPriorAsync(h, priorAmount: 1000m);

        var result = await h.Sut.RecomputeAsync(
            "PRIOR-SQID", DecisionRecomputeReason.PaymentReversed, newMonthlyAmountMdl: 800m);

        result.IsSuccess.Should().BeTrue();
        result.Value.DocumentKindCode.Should().Be(DecisionRecomputeService.DocCodeRecuperare);
        result.Value.Delta.Should().Be(-200m);
        result.Value.NewDocumentSqid.Should().NotBeNull();
    }

    /// <summary>Zero delta yields no new document.</summary>
    [Fact]
    public async Task RecomputeAsync_ZeroDelta_DoesNotEmitDocument()
    {
        var h = Create();
        await SeedPriorAsync(h, priorAmount: 1000m);

        var result = await h.Sut.RecomputeAsync(
            "PRIOR-SQID", DecisionRecomputeReason.LegislativeUpdate, newMonthlyAmountMdl: 1000m);

        result.IsSuccess.Should().BeTrue();
        result.Value.DocumentKindCode.Should().Be(DecisionRecomputeService.DocCodeNoChange);
        result.Value.Delta.Should().Be(0m);
        result.Value.NewDocumentSqid.Should().BeNull();
        (await h.Db.Documents.CountAsync()).Should().Be(0);
    }

    /// <summary>Unknown prior decision returns NotFound.</summary>
    [Fact]
    public async Task RecomputeAsync_UnknownPrior_ReturnsNotFound()
    {
        var h = Create();
        h.Sqids.TryDecode("MISSING").Returns(Result<long>.Success(9999L));

        var result = await h.Sut.RecomputeAsync(
            "MISSING", DecisionRecomputeReason.Other, 500m);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>State-changing recompute emits a Critical audit row.</summary>
    [Fact]
    public async Task RecomputeAsync_OnDelta_EmitsAuditRow()
    {
        var h = Create();
        await SeedPriorAsync(h, priorAmount: 1000m);

        var result = await h.Sut.RecomputeAsync(
            "PRIOR-SQID", DecisionRecomputeReason.BaseAmountChanged, 1100m);

        result.IsSuccess.Should().BeTrue();
        await h.Audit.Received(1).RecordAsync(
            DecisionRecomputeService.AuditRecomputed,
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(ServiceApplication),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>State-changing recompute dispatches the ActionResult notification.</summary>
    [Fact]
    public async Task RecomputeAsync_OnDelta_DispatchesNotification()
    {
        var h = Create();
        await SeedPriorAsync(h, priorAmount: 1000m);

        var result = await h.Sut.RecomputeAsync(
            "PRIOR-SQID", DecisionRecomputeReason.BaseAmountChanged, 1100m);

        result.IsSuccess.Should().BeTrue();
        await h.Triggers.Received(1).DispatchAsync(
            NotificationTriggerKind.ActionResult,
            Arg.Any<NotificationTriggerPayload>(),
            Arg.Any<CancellationToken>());
    }
}
