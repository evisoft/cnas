using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Audit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Audit;

/// <summary>
/// R0196 / TOR CF 23.02 — tests for
/// <see cref="AuditCategoryService"/>. Verifies the CRUD lifecycle, audit
/// emission, and IsActive filter on the list endpoint.
/// </summary>
public sealed class AuditCategoryServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    private static CnasDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-audit-cat-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static ISqidService NewSqids()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(c => $"SQID-{c.Arg<long>()}");
        s.TryDecode(Arg.Any<string>()).Returns(c =>
        {
            var v = c.Arg<string>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return s;
    }

    private static ICallerContext NewCaller()
    {
        var c = Substitute.For<ICallerContext>();
        c.UserId.Returns(1L);
        c.UserSqid.Returns("SQID-1");
        c.SourceIp.Returns("203.0.113.7");
        c.CorrelationId.Returns("corr-audit");
        return c;
    }

    private static IAuditService NewAuditCapturing(out List<string> codes)
    {
        var list = new List<string>();
        codes = list;
        var a = Substitute.For<IAuditService>();
        a.RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                list.Add(c.ArgAt<string>(0));
                return Task.FromResult(Result.Success());
            });
        return a;
    }

    private static AuditCategoryService NewService(CnasDbContext db, IAuditService audit)
        => new(
            db: db,
            read: db,
            clock: new StubClock(),
            sqids: NewSqids(),
            caller: NewCaller(),
            audit: audit,
            createValidator: new AuditCategoryCreateInputValidator(),
            modifyValidator: new AuditCategoryModifyInputValidator(),
            filterValidator: new AuditCategoryFilterValidator());

    private static AuditCategoryCreateInputDto NewCreate(string code = "AUTH") => new(
        Code: code,
        DisplayName: "Authentication & session lifecycle",
        Description: null,
        DefaultSeverity: "Notice");

    [Fact]
    public async Task Create_HappyPath_PersistsRow_AndAudits()
    {
        using var db = CreateContext();
        var audit = NewAuditCapturing(out var codes);
        var svc = NewService(db, audit);

        var result = await svc.CreateAsync(NewCreate(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.AuditCategories.Should().HaveCount(1);
        codes.Should().Contain(IAuditCategoryService.AuditCategoryCreated);
    }

    [Fact]
    public async Task Create_DuplicateCode_Rejected()
    {
        using var db = CreateContext();
        var svc = NewService(db, NewAuditCapturing(out _));

        var first = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var second = await svc.CreateAsync(NewCreate(), CancellationToken.None);
        second.IsSuccess.Should().BeFalse();
        second.ErrorCode.Should().Be(IAuditCategoryService.DuplicateCategoryCodeCode);
    }

    [Fact]
    public async Task GetByCode_AfterCreate_ReturnsRow()
    {
        using var db = CreateContext();
        var svc = NewService(db, NewAuditCapturing(out _));

        var created = await svc.CreateAsync(NewCreate("DB_QUERY"), CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var fetched = await svc.GetByCodeAsync("DB_QUERY", CancellationToken.None);

        fetched.IsSuccess.Should().BeTrue();
        fetched.Value.Code.Should().Be("DB_QUERY");
    }

    [Fact]
    public async Task List_FiltersByIsActive()
    {
        using var db = CreateContext();
        db.AuditCategories.Add(new AuditCategory
        {
            Code = "ACTIVE_ONE",
            DisplayName = "Active",
            DefaultSeverity = AuditSeverity.Information,
            IsActive = true,
            CreatedAtUtc = ClockNow,
            CreatedBy = "seed",
        });
        db.AuditCategories.Add(new AuditCategory
        {
            Code = "INACTIVE_ONE",
            DisplayName = "Inactive",
            DefaultSeverity = AuditSeverity.Information,
            IsActive = false,
            CreatedAtUtc = ClockNow,
            CreatedBy = "seed",
        });
        await db.SaveChangesAsync();

        var svc = NewService(db, NewAuditCapturing(out _));
        var activeOnly = await svc.ListAsync(
            new AuditCategoryFilterDto(IsActive: true), CancellationToken.None);

        activeOnly.IsSuccess.Should().BeTrue();
        activeOnly.Value.Items.Should().HaveCount(1);
        activeOnly.Value.Items[0].Code.Should().Be("ACTIVE_ONE");
    }

    [Fact]
    public async Task DeactivateThenActivate_TogglesIsActive()
    {
        using var db = CreateContext();
        var svc = NewService(db, NewAuditCapturing(out var codes));

        var created = await svc.CreateAsync(NewCreate("APPROVAL"), CancellationToken.None);
        var sqid = created.Value.Id;

        var deact = await svc.DeactivateAsync(sqid, CancellationToken.None);
        deact.IsSuccess.Should().BeTrue();
        deact.Value.IsActive.Should().BeFalse();

        var act = await svc.ActivateAsync(sqid, CancellationToken.None);
        act.IsSuccess.Should().BeTrue();
        act.Value.IsActive.Should().BeTrue();

        codes.Should().Contain(IAuditCategoryService.AuditCategoryTransitioned);
    }
}
