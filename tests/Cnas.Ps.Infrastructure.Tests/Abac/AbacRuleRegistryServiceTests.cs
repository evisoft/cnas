using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cnas.Ps.Application.Abac;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Application.Validators;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services.Abac;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Abac;

/// <summary>
/// R2271 / TOR SEC 025 — tests for <see cref="AbacRuleRegistryService"/>.
/// Covers the create happy-path with audit, the parse-before-persist guard,
/// the atomic bulk-reorder, and the dry-run test endpoint.
/// </summary>
public sealed class AbacRuleRegistryServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    private sealed class StubClock : ICnasTimeProvider
    {
        public DateTime UtcNow => ClockNow;
    }

    private static CnasDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-abac-reg-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CnasDbContext(opts);
    }

    private static ISqidService NewSqidStub()
    {
        var s = Substitute.For<ISqidService>();
        s.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        s.TryDecode(Arg.Any<string?>()).Returns(call =>
        {
            var v = call.Arg<string?>();
            if (v is not null && v.StartsWith("SQID-", StringComparison.Ordinal)
                && long.TryParse(v["SQID-".Length..], out var id))
            {
                return Result<long>.Success(id);
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "n/a");
        });
        return s;
    }

    private static ICallerContext NewCaller(long userId)
    {
        var caller = Substitute.For<ICallerContext>();
        caller.UserId.Returns((long?)userId);
        caller.UserSqid.Returns($"SQID-{userId}");
        caller.SourceIp.Returns("127.0.0.1");
        caller.CorrelationId.Returns("corr-test");
        return caller;
    }

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
                Arg.Any<CancellationToken>())
            .Returns(System.Threading.Tasks.Task.FromResult(Result.Success()));
        return audit;
    }

    private static (AbacRuleRegistryService Service, CnasDbContext Db, IAuditService Audit, IAbacRuleEvaluator Evaluator)
        Build(long callerId = 7L)
    {
        var db = NewContext();
        var parser = new AbacExpressionParser();
        var services = new ServiceCollection();
        services.AddSingleton<ICnasDbContext>(db);
        var provider = services.BuildServiceProvider();
        var evaluator = new AbacRuleEvaluator(provider, parser, NewSqidStub(), NullLogger<AbacRuleEvaluator>.Instance);
        var audit = NewAudit();
        var service = new AbacRuleRegistryService(
            db,
            NewSqidStub(),
            new StubClock(),
            NewCaller(callerId),
            audit,
            parser,
            evaluator,
            new AbacRuleSetCreateInputValidator(),
            new AbacRuleSetModifyInputValidator(),
            new AbacRuleInputValidator(parser),
            new AbacRuleReorderInputValidator(),
            new AbacRuleSetFilterValidator(),
            new AbacRuleReasonInputValidator(),
            new AbacExpressionTestInputValidator());
        return (service, db, audit, evaluator);
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateRuleSet_HappyPath_PersistsAndEmitsCriticalAudit()
    {
        var (svc, db, audit, _) = Build();

        var result = await svc.CreateRuleSetAsync(
            new AbacRuleSetCreateInputDto("DOSSIER.READ", "Read dossiers", "Sample", "Deny"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PolicyName.Should().Be("DOSSIER.READ");
        result.Value.DefaultEffect.Should().Be("Deny");
        (await db.AbacRuleSets.SingleAsync()).PolicyName.Should().Be("DOSSIER.READ");

        await audit.Received().RecordAsync(
            "ABAC.RULE_SET_CREATED",
            AuditSeverity.Critical,
            Arg.Any<string>(),
            nameof(AbacRuleSet),
            Arg.Any<long?>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async System.Threading.Tasks.Task AddRule_WithBadExpression_ReturnsAbacParseError()
    {
        var (svc, _, _, _) = Build();
        var created = await svc.CreateRuleSetAsync(
            new AbacRuleSetCreateInputDto("DOSSIER.READ", "Read dossiers", null, "Deny"),
            CancellationToken.None);
        created.IsSuccess.Should().BeTrue();

        var result = await svc.AddRuleAsync(
            created.Value.Id,
            new AbacRuleInputDto(0, "Allow", "this is not a valid expression", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AbacParseError);
    }

    [Fact]
    public async System.Threading.Tasks.Task ReorderRules_AtomicallyAssignsNewOrderIndices()
    {
        var (svc, db, _, _) = Build();
        var rs = await svc.CreateRuleSetAsync(
            new AbacRuleSetCreateInputDto("DOSSIER.READ", "Read dossiers", null, "Deny"),
            CancellationToken.None);
        var r1 = await svc.AddRuleAsync(rs.Value.Id,
            new AbacRuleInputDto(0, "Allow", "subject.regionCode == \"MD-CH\"", null), CancellationToken.None);
        var r2 = await svc.AddRuleAsync(rs.Value.Id,
            new AbacRuleInputDto(1, "Deny", "subject.regionCode == \"MD-BC\"", null), CancellationToken.None);
        r1.IsSuccess.Should().BeTrue();
        r2.IsSuccess.Should().BeTrue();

        var result = await svc.ReorderRulesAsync(
            rs.Value.Id,
            new[]
            {
                new AbacRuleReorderInputDto(r1.Value.Id, 5),
                new AbacRuleReorderInputDto(r2.Value.Id, 3),
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var ordered = await db.AbacRules.OrderBy(r => r.OrderIndex).ToListAsync();
        ordered.Should().HaveCount(2);
        ordered[0].OrderIndex.Should().Be(3); // r2
        ordered[1].OrderIndex.Should().Be(5); // r1
    }

    [Fact]
    public async System.Threading.Tasks.Task TestExpression_ReturnsDeterministicDecision()
    {
        var (svc, _, _, _) = Build();
        var rs = await svc.CreateRuleSetAsync(
            new AbacRuleSetCreateInputDto("DOSSIER.READ", "Read dossiers", null, "Deny"),
            CancellationToken.None);
        await svc.AddRuleAsync(rs.Value.Id,
            new AbacRuleInputDto(0, "Allow", "subject.regionCode == \"MD-CH\"", null),
            CancellationToken.None);

        var input = new AbacExpressionTestInputDto(
            "DOSSIER.READ",
            Subject: new Dictionary<string, object?> { ["regionCode"] = "MD-CH" },
            Resource: new Dictionary<string, object?>(),
            Environment: new Dictionary<string, object?>(),
            Action: new Dictionary<string, object?>());
        var result = await svc.TestExpressionAsync(input, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Effect.Should().Be("Allow");
        result.Value.MatchedRuleOrderIndex.Should().Be(0);
    }
}
