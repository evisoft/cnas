using System;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Decisions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0141 / TOR CF 15.03 — TDD coverage of
/// <see cref="ServicePassportRulesEditorService"/>. Each fact pins one
/// behaviour of the editor (list happy / unknown passport / upsert new /
/// upsert existing / delete + persistence).
/// </summary>
public sealed class ServicePassportRulesEditorServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-rules-editor-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IAuditService NewAudit()
    {
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
                Arg.Any<System.Threading.CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        return audit;
    }

    private static ICallerContext NewCaller()
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("admin-sqid");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-test");
        return caller;
    }

    private static ICnasTimeProvider NewClock()
    {
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);
        return clock;
    }

    private static ServicePassportRulesEditorService Build(
        CnasDbContext db,
        IAuditService? audit = null,
        ICallerContext? caller = null)
    {
        return new ServicePassportRulesEditorService(
            db,
            NewClock(),
            caller ?? NewCaller(),
            audit ?? NewAudit(),
            new BusinessRuleInputValidator(),
            new JsonRulesDecisionEngine());
    }

    private static async Task<ServicePassport> SeedPassportAsync(
        CnasDbContext db,
        string code,
        string? rulesJson = null)
    {
        var p = new ServicePassport
        {
            Code = code,
            NameRo = "Test passport",
            DescriptionRo = "Test",
            FormSchemaJson = """{"type":"object"}""",
            WorkflowCode = "WF-TEST",
            MaxProcessingDays = 30,
            IsEnabled = true,
            IsActive = true,
            IsCurrent = true,
            Version = 1,
            DecisionRulesJson = rulesJson ?? """{"code":"TEST"}""",
            CreatedAtUtc = ClockNow,
        };
        db.ServicePassports.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static BusinessRuleInputDto GoodInput(string name = "Reject minors") => new(
        Id: null,
        Name: name,
        ApplicantType: BusinessRuleApplicantType.Natural,
        ConditionJson: """{"rule":"fact-less-than","fact":"ageYears","value":18}""",
        DecisionOutcome: BusinessRuleDecisionOutcome.Rejected,
        Notes: null);

    [Fact]
    public async Task ListRulesAsync_PassportExistsButHasNoRules_ReturnsEmptyList()
    {
        using var db = CreateContext();
        await SeedPassportAsync(db, "SP-A");

        var svc = Build(db);
        var result = await svc.ListRulesAsync("SP-A");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task ListRulesAsync_UnknownPassport_ReturnsNotFound()
    {
        using var db = CreateContext();
        var svc = Build(db);

        var result = await svc.ListRulesAsync("MISSING");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task UpsertRuleAsync_CreatesNewRule_PersistsAndReturnsAddressableId()
    {
        using var db = CreateContext();
        await SeedPassportAsync(db, "SP-B");

        var svc = Build(db);
        var input = GoodInput();
        var upsert = await svc.UpsertRuleAsync("SP-B", input);
        upsert.IsSuccess.Should().BeTrue();
        upsert.Value.Id.Should().NotBeNullOrEmpty();

        var list = await svc.ListRulesAsync("SP-B");
        list.IsSuccess.Should().BeTrue();
        list.Value.Should().ContainSingle();
        list.Value[0].Id.Should().Be(upsert.Value.Id);
        list.Value[0].Name.Should().Be("Reject minors");
        list.Value[0].ApplicantType.Should().Be(BusinessRuleApplicantType.Natural);
        list.Value[0].DecisionOutcome.Should().Be(BusinessRuleDecisionOutcome.Rejected);
    }

    [Fact]
    public async Task UpsertRuleAsync_UpdatesExistingRuleById_KeepsListSizeOne()
    {
        using var db = CreateContext();
        await SeedPassportAsync(db, "SP-C");
        var svc = Build(db);

        var first = await svc.UpsertRuleAsync("SP-C", GoodInput());
        first.IsSuccess.Should().BeTrue();

        var update = new BusinessRuleInputDto(
            Id: first.Value.Id,
            Name: "Reject minors (revised)",
            ApplicantType: BusinessRuleApplicantType.Natural,
            ConditionJson: """{"rule":"fact-less-than","fact":"ageYears","value":16}""",
            DecisionOutcome: BusinessRuleDecisionOutcome.RequiresReview,
            Notes: "Lowered age cutoff to 16.");

        var second = await svc.UpsertRuleAsync("SP-C", update);
        second.IsSuccess.Should().BeTrue();

        var list = await svc.ListRulesAsync("SP-C");
        list.Value.Should().ContainSingle();
        list.Value[0].Name.Should().Be("Reject minors (revised)");
        list.Value[0].DecisionOutcome.Should().Be(BusinessRuleDecisionOutcome.RequiresReview);
        list.Value[0].Notes.Should().Be("Lowered age cutoff to 16.");
    }

    [Fact]
    public async Task UpsertRuleAsync_InputFailsValidator_ReturnsValidationFailed()
    {
        using var db = CreateContext();
        await SeedPassportAsync(db, "SP-D");

        var svc = Build(db);
        var bad = GoodInput() with { ConditionJson = "{not-json" };
        var result = await svc.UpsertRuleAsync("SP-D", bad);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task UpsertRuleAsync_UnknownPassport_ReturnsNotFound()
    {
        using var db = CreateContext();
        var svc = Build(db);

        var result = await svc.UpsertRuleAsync("MISSING", GoodInput());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task DeleteRuleAsync_RemovesRule_AndPersistsAcrossList()
    {
        using var db = CreateContext();
        await SeedPassportAsync(db, "SP-E");
        var svc = Build(db);

        var created = await svc.UpsertRuleAsync("SP-E", GoodInput());
        created.IsSuccess.Should().BeTrue();

        var deleted = await svc.DeleteRuleAsync("SP-E", created.Value.Id);
        deleted.IsSuccess.Should().BeTrue();

        var list = await svc.ListRulesAsync("SP-E");
        list.IsSuccess.Should().BeTrue();
        list.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteRuleAsync_UnknownRule_ReturnsNotFound()
    {
        using var db = CreateContext();
        await SeedPassportAsync(db, "SP-F");
        var svc = Build(db);

        var result = await svc.DeleteRuleAsync("SP-F", "DEFINITELYNOTEXIST");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
