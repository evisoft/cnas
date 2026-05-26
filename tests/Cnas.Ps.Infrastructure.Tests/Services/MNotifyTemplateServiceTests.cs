using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.MNotify;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0115 / TOR CF 14.07 — integration tests for
/// <see cref="MNotifyTemplateService"/>.
/// </summary>
public sealed class MNotifyTemplateServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-mnotify-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record Harness(CnasDbContext Db, MNotifyTemplateService Sut, IAuditService Audit, ISqidService Sqids);

    private static Harness Create()
    {
        var db = CreateContext();
        var roDb = Substitute.For<IReadOnlyCnasDbContext>();
        roDb.MNotifyTemplates.Returns(_ => db.MNotifyTemplates.AsNoTracking());

        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");

        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-USER");
        caller.UserId.Returns(11L);
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr");

        var sut = new MNotifyTemplateService(db, roDb, sqids, new StubClock(ClockNow), caller, audit);
        return new Harness(db, sut, audit, sqids);
    }

    /// <summary>Upsert inserts a new row.</summary>
    [Fact]
    public async Task UpsertAsync_NewCode_InsertsRow()
    {
        var h = Create();
        var input = new MNotifyTemplateInputDto(
            "WORKFLOW.TASK.ASSIGNED", MNotifyChannelKindDto.Email, "Subj", "Body");

        var result = await h.Sut.UpsertAsync(input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("WORKFLOW.TASK.ASSIGNED");
        (await h.Db.MNotifyTemplates.CountAsync()).Should().Be(1);
    }

    /// <summary>Upsert updates an existing row identified by Code.</summary>
    [Fact]
    public async Task UpsertAsync_ExistingCode_UpdatesRow()
    {
        var h = Create();
        await h.Sut.UpsertAsync(new MNotifyTemplateInputDto(
            "WORKFLOW.TASK.ASSIGNED", MNotifyChannelKindDto.Email, "Subj", "Body"));

        var result = await h.Sut.UpsertAsync(new MNotifyTemplateInputDto(
            "WORKFLOW.TASK.ASSIGNED", MNotifyChannelKindDto.Email, "Updated", "New body"));

        result.IsSuccess.Should().BeTrue();
        (await h.Db.MNotifyTemplates.CountAsync()).Should().Be(1);
        var row = await h.Db.MNotifyTemplates.SingleAsync();
        row.Subject.Should().Be("Updated");
        row.BodyMarkdown.Should().Be("New body");
    }

    /// <summary>Upsert emits the audit row.</summary>
    [Fact]
    public async Task UpsertAsync_OnInsert_EmitsAuditRow()
    {
        var h = Create();
        var input = new MNotifyTemplateInputDto(
            "AUTH.LOGIN.SUCCESS", MNotifyChannelKindDto.Sms, null, "Logged in.");

        var result = await h.Sut.UpsertAsync(input);

        result.IsSuccess.Should().BeTrue();
        await h.Audit.Received(1).RecordAsync(
            MNotifyTemplateService.AuditUpserted,
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(MNotifyTemplate),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>ListAsync orders by Code and respects the include-inactive flag.</summary>
    [Fact]
    public async Task ListAsync_ExcludesInactiveByDefault()
    {
        var h = Create();
        await h.Sut.UpsertAsync(new MNotifyTemplateInputDto(
            "B.CODE", MNotifyChannelKindDto.Sms, null, "Body"));
        await h.Sut.UpsertAsync(new MNotifyTemplateInputDto(
            "A.CODE", MNotifyChannelKindDto.Sms, null, "Body"));
        // Deactivate "A.CODE"
        var firstRow = await h.Db.MNotifyTemplates.SingleAsync(r => r.Code == "A.CODE");
        firstRow.IsActive = false;
        await h.Db.SaveChangesAsync();

        var result = await h.Sut.ListAsync(includeInactive: false);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle()
            .Which.Code.Should().Be("B.CODE");
    }

    /// <summary>Deactivate flips IsActive to false and is idempotent.</summary>
    [Fact]
    public async Task DeactivateAsync_FlipsAndIsIdempotent()
    {
        var h = Create();
        await h.Sut.UpsertAsync(new MNotifyTemplateInputDto(
            "DEACT.CODE", MNotifyChannelKindDto.Sms, null, "Body"));
        var row = await h.Db.MNotifyTemplates.SingleAsync();
        var sqid = $"SQID-{row.Id}";
        h.Sqids.TryDecode(sqid).Returns(Result<long>.Success(row.Id));

        var first = await h.Sut.DeactivateAsync(sqid);
        var second = await h.Sut.DeactivateAsync(sqid);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        (await h.Db.MNotifyTemplates.SingleAsync()).IsActive.Should().BeFalse();
    }

    /// <summary>Deactivate on unknown id returns NotFound.</summary>
    [Fact]
    public async Task DeactivateAsync_Unknown_ReturnsNotFound()
    {
        var h = Create();
        h.Sqids.TryDecode("SQID-99").Returns(Result<long>.Success(99L));

        var result = await h.Sut.DeactivateAsync("SQID-99");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
