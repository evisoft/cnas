using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.LaborBooklet;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0922 / TOR Annex 2 §8.2.4 — integration tests for
/// <see cref="Pre1999StagiuService"/>. Uses EF Core InMemory for the persistence
/// backend and NSubstitute for the surrounding collaborators (sqid, audit,
/// caller).
/// </summary>
public sealed class Pre1999StagiuServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);

    private const string ValidIdnp = "2000123456782";

    private static CnasDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-pre1999stag-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

    /// <summary>Stub clock returning a fixed instant.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed record Harness(
        CnasDbContext Db,
        Pre1999StagiuService Service,
        IAuditService Audit,
        ISqidService Sqids,
        ICallerContext Caller);

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

        var validator = new Pre1999StagiuInputValidator();
        var service = new Pre1999StagiuService(
            db, new StubClock(ClockNow), sqids, caller, audit, validator);
        return new Harness(db, service, audit, sqids, caller);
    }

    private static async Task<InsuredPerson> SeedInsuredAsync(Harness h, string sqid = "INS-SQID")
    {
        var insured = new InsuredPerson
        {
            Idnp = ValidIdnp,
            IdnpHash = IdHashHelper.Hash(ValidIdnp),
            LastName = "Popescu",
            FirstName = "Ion",
            BirthDate = new DateOnly(1955, 1, 1),
            RegisteredAtUtc = ClockNow.AddDays(-30),
            CreatedAtUtc = ClockNow.AddDays(-30),
            IsActive = true,
        };
        h.Db.InsuredPersons.Add(insured);
        await h.Db.SaveChangesAsync();
        h.Sqids.TryDecode(sqid).Returns(Result<long>.Success(insured.Id));
        return insured;
    }

    /// <summary>Happy-path append round-trips through the projection.</summary>
    [Fact]
    public async Task AppendAsync_HappyPath_PersistsAndReturnsDto()
    {
        var h = Create();
        await SeedInsuredAsync(h);
        var input = new Pre1999StagiuInputDto(
            FromDate: new DateOnly(1985, 1, 1),
            ToDate: new DateOnly(1990, 6, 30),
            Years: 5,
            Months: 5,
            Days: 29,
            Source: "Carnet de muncă series A",
            Notes: null);

        var result = await h.Service.AppendAsync("INS-SQID", input);

        result.IsSuccess.Should().BeTrue();
        result.Value.Years.Should().Be(5);
        result.Value.Months.Should().Be(5);
        result.Value.Days.Should().Be(29);
        result.Value.InsuredPersonSqid.Should().Be("INS-SQID");

        var persisted = await h.Db.Pre1999StagiuRecords.SingleAsync();
        persisted.Source.Should().Be("Carnet de muncă series A");
        persisted.FromDate.Should().Be(new DateOnly(1985, 1, 1));
    }

    /// <summary>Post-1999 FromDate fails with ValidationFailed.</summary>
    [Fact]
    public async Task AppendAsync_Post1999FromDate_ReturnsValidationFailed()
    {
        var h = Create();
        await SeedInsuredAsync(h);
        var input = new Pre1999StagiuInputDto(
            FromDate: new DateOnly(1999, 6, 1),
            ToDate: new DateOnly(2000, 1, 1),
            Years: 1,
            Months: 0,
            Days: 0);

        var result = await h.Service.AppendAsync("INS-SQID", input);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>List returns rows ordered by FromDate ascending.</summary>
    [Fact]
    public async Task ListAsync_MultipleRows_OrdersByFromDateAscending()
    {
        var h = Create();
        await SeedInsuredAsync(h);
        await h.Service.AppendAsync("INS-SQID", new Pre1999StagiuInputDto(
            new DateOnly(1995, 1, 1), new DateOnly(1996, 6, 1), 1, 5, 0));
        await h.Service.AppendAsync("INS-SQID", new Pre1999StagiuInputDto(
            new DateOnly(1985, 1, 1), new DateOnly(1990, 12, 1), 5, 11, 0));

        var result = await h.Service.ListAsync("INS-SQID");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].FromDate.Should().Be(new DateOnly(1985, 1, 1));
        result.Value[1].FromDate.Should().Be(new DateOnly(1995, 1, 1));
    }

    /// <summary>Remove happy-path soft-deletes the row.</summary>
    [Fact]
    public async Task RemoveAsync_HappyPath_FlipsIsActiveAndAudits()
    {
        var h = Create();
        await SeedInsuredAsync(h);
        var appended = await h.Service.AppendAsync("INS-SQID", new Pre1999StagiuInputDto(
            new DateOnly(1990, 1, 1), new DateOnly(1992, 1, 1), 2, 0, 0));
        appended.IsSuccess.Should().BeTrue();
        var row = await h.Db.Pre1999StagiuRecords.SingleAsync();
        h.Sqids.TryDecode("REC-SQID").Returns(Result<long>.Success(row.Id));

        var result = await h.Service.RemoveAsync("REC-SQID");

        result.IsSuccess.Should().BeTrue();
        var reloaded = await h.Db.Pre1999StagiuRecords.IgnoreQueryFilters().SingleAsync();
        reloaded.IsActive.Should().BeFalse();
    }

    /// <summary>Remove with unknown Sqid returns NotFound.</summary>
    [Fact]
    public async Task RemoveAsync_UnknownRecord_ReturnsNotFound()
    {
        var h = Create();
        h.Sqids.TryDecode("MISSING").Returns(Result<long>.Success(9999L));

        var result = await h.Service.RemoveAsync("MISSING");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    /// <summary>Append emits a Notice audit record.</summary>
    [Fact]
    public async Task AppendAsync_EmitsAuditRow()
    {
        var h = Create();
        await SeedInsuredAsync(h);
        var input = new Pre1999StagiuInputDto(
            new DateOnly(1985, 1, 1), new DateOnly(1990, 6, 30), 5, 5, 29);

        var result = await h.Service.AppendAsync("INS-SQID", input);

        result.IsSuccess.Should().BeTrue();
        await h.Audit.Received(1).RecordAsync(
            Pre1999StagiuService.AuditAppended,
            AuditSeverity.Notice,
            Arg.Any<string>(),
            nameof(Pre1999StagiuRecord),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
