using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R2163 / INT 004 — TDD coverage for <see cref="ServiceCatalogConfigService"/>. Backed
/// by an EF Core InMemory store. Asserts the schema-driven provisioning + retirement
/// contract from <see cref="IServiceCatalogConfigService"/> plus the audit / classifier-
/// registration side effects.
/// </summary>
public sealed class ServiceCatalogConfigServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 25, 10, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-svc-catalog-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>Records every audit call so tests can assert event-code emission.</summary>
    private sealed record AuditCall(string EventCode, AuditSeverity Severity, string ActorId, string? Details);

    private static (ServiceCatalogConfigService Sut, IAuditService Audit, List<AuditCall> AuditCalls, IClassifierService Classifiers)
        Build(CnasDbContext db)
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        var clock = Substitute.For<ICnasTimeProvider>();
        clock.UtcNow.Returns(ClockNow);
        var caller = Substitute.For<ICallerContext>();
        caller.UserSqid.Returns("SQID-ADMIN");

        var auditCalls = new List<AuditCall>();
        var audit = Substitute.For<IAuditService>();
        audit
            .RecordAsync(
                Arg.Any<string>(), Arg.Any<AuditSeverity>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                auditCalls.Add(new AuditCall(
                    call.ArgAt<string>(0),
                    call.ArgAt<AuditSeverity>(1),
                    call.ArgAt<string>(2),
                    call.ArgAt<string>(5)));
                return Task.FromResult(Result.Success());
            });

        var workflows = Substitute.For<IWorkflowConfigurationService>();
        workflows.GetDefinitionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("{}"));
        workflows.SaveDefinitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var classifiers = Substitute.For<IClassifierService>();
        classifiers.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<ClassifierRow>>.Success([
                new ClassifierRow("CAEM", "01.11", "Sample", null, null, null, "manual")
            ]));

        var sut = new ServiceCatalogConfigService(db, sqids, clock, caller, audit, workflows, classifiers);
        return (sut, audit, auditCalls, classifiers);
    }

    /// <summary>Shared static schema list to avoid CA1861 inline-array allocations.</summary>
    private static readonly IReadOnlyList<string> SampleSchemes = ["CAEM"];

    private static NewServiceProvisionInputDto SampleInput(string code = "SP-NEW-INT004") => new(
        Code: code,
        NameRo: "Serviciu nou INT 004",
        NameEn: "New INT 004 service",
        NameRu: "Новый сервис INT 004",
        DescriptionRo: "Descriere provisioned via INT 004.",
        WorkflowCode: "WF-INT004",
        MaxProcessingDays: 30,
        FormSchemaJson: "{\"type\":\"object\",\"properties\":{\"idnp\":{\"type\":\"string\"}}}",
        DecisionRulesJson: "{}",
        ClassifierSchemes: SampleSchemes,
        IsEnabled: true,
        IsProactive: false);

    [Fact]
    public async Task ProvisionAsync_HappyPath_CreatesPassportAndAudits()
    {
        using var db = CreateContext();
        var (sut, _, auditCalls, _) = Build(db);

        var result = await sut.ProvisionAsync(SampleInput(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("SP-NEW-INT004");
        result.Value.Version.Should().Be(1);
        result.Value.WorkflowCode.Should().Be("WF-INT004");

        var passports = await db.ServicePassports.ToListAsync();
        passports.Should().ContainSingle(p => p.Code == "SP-NEW-INT004");
        passports[0].IsCurrent.Should().BeTrue();
        passports[0].Version.Should().Be(1);
        passports[0].IsEnabled.Should().BeTrue();

        auditCalls.Should().ContainSingle(c => c.EventCode == "SERVICE.PROVISIONED");
        auditCalls[0].Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public async Task ProvisionAsync_DuplicateCode_ReturnsConflict()
    {
        using var db = CreateContext();
        db.ServicePassports.Add(new ServicePassport
        {
            Code = "SP-NEW-INT004",
            NameRo = "Existing",
            DescriptionRo = "Existing",
            WorkflowCode = "WF-OTHER",
            IsCurrent = true,
            IsActive = true,
            Version = 1,
            CreatedAtUtc = ClockNow,
        });
        await db.SaveChangesAsync();

        var (sut, _, _, _) = Build(db);

        var result = await sut.ProvisionAsync(SampleInput(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task ProvisionAsync_InvalidJsonSchema_ReturnsValidationFailed()
    {
        using var db = CreateContext();
        var (sut, _, _, _) = Build(db);
        var input = SampleInput() with { FormSchemaJson = "{not-json" };

        var result = await sut.ProvisionAsync(input, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        (await db.ServicePassports.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RetireAsync_HappyPath_DeactivatesAndAudits()
    {
        using var db = CreateContext();
        db.ServicePassports.Add(new ServicePassport
        {
            Code = "SP-NEW-INT004",
            NameRo = "Existing",
            DescriptionRo = "Existing",
            WorkflowCode = "WF-OTHER",
            IsCurrent = true,
            IsActive = true,
            IsEnabled = true,
            Version = 1,
            CreatedAtUtc = ClockNow,
        });
        await db.SaveChangesAsync();

        var (sut, _, auditCalls, _) = Build(db);

        var result = await sut.RetireAsync("SP-NEW-INT004", "End-of-life", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var passport = await db.ServicePassports.SingleAsync(p => p.Code == "SP-NEW-INT004");
        passport.IsEnabled.Should().BeFalse();

        auditCalls.Should().ContainSingle(c => c.EventCode == "SERVICE.RETIRED");
        auditCalls[0].Severity.Should().Be(AuditSeverity.Critical);
        auditCalls[0].Details.Should().Contain("End-of-life");
    }

    [Fact]
    public async Task RetireAsync_UnknownPassport_ReturnsNotFound()
    {
        using var db = CreateContext();
        var (sut, _, _, _) = Build(db);

        var result = await sut.RetireAsync("SP-DOES-NOT-EXIST", "End-of-life", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task RetireAsync_EmptyReason_ReturnsValidationFailed()
    {
        using var db = CreateContext();
        var (sut, _, _, _) = Build(db);

        var result = await sut.RetireAsync("SP-NEW-INT004", "  ", CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }
}
