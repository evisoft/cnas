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
/// R1505 / TOR §3.7-F — integration tests for <see cref="RecoveryDecisionService"/>.
/// Uses EF Core InMemory + NSubstitute for the surrounding collaborators.
/// </summary>
public sealed class RecoveryDecisionServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-recovery-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record Harness(
        CnasDbContext Db,
        RecoveryDecisionService Sut,
        IAuditService Audit,
        ISqidService Sqids,
        INotificationTriggerDispatcher Triggers,
        Solicitant Solicitant);

    private static async Task<Harness> CreateAsync()
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

        var solicitant = new Solicitant
        {
            CreatedAtUtc = ClockNow,
            NationalId = "2000123456782",
            Kind = ApplicantKind.NaturalPerson,
            DisplayName = "Ion Popescu",
            PreferredLanguage = "ro",
            IsActive = true,
        };
        db.Solicitants.Add(solicitant);
        await db.SaveChangesAsync();
        // Wire the solicitant Sqid round-trip.
        sqids.TryDecode($"SQID-{solicitant.Id}").Returns(Result<long>.Success(solicitant.Id));

        var sut = new RecoveryDecisionService(
            db, sqids, new StubClock(ClockNow), caller, audit,
            NullLogger<RecoveryDecisionService>.Instance, triggers);

        return new Harness(db, sut, audit, sqids, triggers, solicitant);
    }

    /// <summary>Happy-path initiation persists a Document and returns the DTO.</summary>
    [Fact]
    public async Task InitiateAsync_HappyPath_PersistsDocumentAndAudits()
    {
        var h = await CreateAsync();

        var result = await h.Sut.InitiateAsync(
            $"SQID-{h.Solicitant.Id}",
            amount: 1500m,
            reason: "Sumă plătită necuvenit",
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(RecoveryDecisionStatus.Initiated);
        result.Value.AmountMdl.Should().Be(1500m);
        result.Value.RecoveredAmountMdl.Should().Be(0m);

        (await h.Db.Documents.CountAsync()).Should().Be(1);

        await h.Audit.Received(1).RecordAsync(
            RecoveryDecisionService.AuditInitiated,
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(Document),
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

    /// <summary>Non-positive amount is rejected with ValidationFailed.</summary>
    [Fact]
    public async Task InitiateAsync_NonPositiveAmount_ReturnsValidationFailed()
    {
        var h = await CreateAsync();

        var result = await h.Sut.InitiateAsync(
            $"SQID-{h.Solicitant.Id}", amount: 0m, reason: "valid reason",
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await h.Db.Documents.CountAsync()).Should().Be(0);
    }

    /// <summary>Acknowledge transitions status to Acknowledged.</summary>
    [Fact]
    public async Task MarkAcknowledgedAsync_TransitionsState()
    {
        var h = await CreateAsync();
        var init = await h.Sut.InitiateAsync(
            $"SQID-{h.Solicitant.Id}", 1000m, "valid reason", CancellationToken.None);
        init.IsSuccess.Should().BeTrue();
        h.Sqids.TryDecode(init.Value.Sqid).Returns(Result<long>.Success(long.Parse(init.Value.Sqid["SQID-".Length..])));

        var ack = await h.Sut.MarkAcknowledgedAsync(init.Value.Sqid, CancellationToken.None);
        ack.IsSuccess.Should().BeTrue();

        var doc = await h.Db.Documents.SingleAsync();
        doc.Verdict.Should().Be((int)RecoveryDecisionStatus.Acknowledged);
        doc.VerdictAtUtc.Should().Be(ClockNow);
    }

    /// <summary>A partial recovery transitions to PartiallyRecovered.</summary>
    [Fact]
    public async Task MarkRecoveredAsync_PartialAmount_TransitionsToPartial()
    {
        var h = await CreateAsync();
        var init = await h.Sut.InitiateAsync(
            $"SQID-{h.Solicitant.Id}", 1000m, "valid reason", CancellationToken.None);
        h.Sqids.TryDecode(init.Value.Sqid).Returns(Result<long>.Success(long.Parse(init.Value.Sqid["SQID-".Length..])));

        var rec = await h.Sut.MarkRecoveredAsync(init.Value.Sqid, recoveredAmount: 400m, CancellationToken.None);
        rec.IsSuccess.Should().BeTrue();

        var doc = await h.Db.Documents.SingleAsync();
        doc.Verdict.Should().Be((int)RecoveryDecisionStatus.PartiallyRecovered);
    }

    /// <summary>
    /// iter-149 — a recovery amount that would push RecoveredSoFar above the
    /// decision Amount is rejected with ValidationFailed before any persistence.
    /// Prevents an over-paid envelope from flipping to FullyRecovered with an
    /// internally inconsistent total.
    /// </summary>
    [Fact]
    public async Task MarkRecoveredAsync_OverRecovery_ReturnsValidationFailed()
    {
        var h = await CreateAsync();
        var init = await h.Sut.InitiateAsync(
            $"SQID-{h.Solicitant.Id}", 1000m, "valid reason", CancellationToken.None);
        h.Sqids.TryDecode(init.Value.Sqid).Returns(Result<long>.Success(long.Parse(init.Value.Sqid["SQID-".Length..])));

        var result = await h.Sut.MarkRecoveredAsync(init.Value.Sqid, recoveredAmount: 1500m, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        // No state change — the document remains in its prior verdict (default 0).
        var doc = await h.Db.Documents.SingleAsync();
        doc.Verdict.Should().NotBe((int)RecoveryDecisionStatus.FullyRecovered);
    }

    /// <summary>A full recovery transitions to FullyRecovered.</summary>
    [Fact]
    public async Task MarkRecoveredAsync_FullAmount_TransitionsToFull()
    {
        var h = await CreateAsync();
        var init = await h.Sut.InitiateAsync(
            $"SQID-{h.Solicitant.Id}", 1000m, "valid reason", CancellationToken.None);
        h.Sqids.TryDecode(init.Value.Sqid).Returns(Result<long>.Success(long.Parse(init.Value.Sqid["SQID-".Length..])));

        var rec = await h.Sut.MarkRecoveredAsync(init.Value.Sqid, recoveredAmount: 1000m, CancellationToken.None);
        rec.IsSuccess.Should().BeTrue();

        var doc = await h.Db.Documents.SingleAsync();
        doc.Verdict.Should().Be((int)RecoveryDecisionStatus.FullyRecovered);
    }

    /// <summary>Double-acknowledge on an already-acknowledged decision is an idempotent success.</summary>
    [Fact]
    public async Task MarkAcknowledgedAsync_Twice_IsIdempotent()
    {
        var h = await CreateAsync();
        var init = await h.Sut.InitiateAsync(
            $"SQID-{h.Solicitant.Id}", 1000m, "valid reason", CancellationToken.None);
        h.Sqids.TryDecode(init.Value.Sqid).Returns(Result<long>.Success(long.Parse(init.Value.Sqid["SQID-".Length..])));

        (await h.Sut.MarkAcknowledgedAsync(init.Value.Sqid, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var second = await h.Sut.MarkAcknowledgedAsync(init.Value.Sqid, CancellationToken.None);
        second.IsSuccess.Should().BeTrue();

        // Exactly one acknowledge-audit row across both calls.
        await h.Audit.Received(1).RecordAsync(
            RecoveryDecisionService.AuditAcknowledged,
            Arg.Any<AuditSeverity>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
