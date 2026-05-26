using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Calculations;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// R0143 / CF 17.19 — TDD coverage of <see cref="ServicePassportConfigMatrixService"/>.
/// Backed by an EF Core InMemory store.
/// </summary>
public sealed class ServicePassportConfigMatrixServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc);

    private static CnasDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CnasDbContext>()
            .UseInMemoryDatabase($"cnas-cfgmatrix-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ServicePassportConfigMatrixService Build(CnasDbContext db)
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        var evaluator = new ShuntingYardExpressionEvaluator();
        return new ServicePassportConfigMatrixService(db, sqids, evaluator);
    }

    private static async Task<ServicePassport> SeedPassportAsync(
        CnasDbContext db,
        string code,
        string? mandatoryJson = null,
        string? calcJson = null)
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
            DecisionRulesJson = """{"code":"TEST"}""",
            MandatoryAttachmentsJson = mandatoryJson,
            CalcFormulasJson = calcJson,
            CreatedAtUtc = ClockNow,
        };
        db.ServicePassports.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task GetMatrixAsync_PassportExists_ReturnsAllEightColumns()
    {
        using var db = CreateContext();
        await SeedPassportAsync(db, "SP-X");

        var svc = Build(db);
        var result = await svc.GetMatrixAsync("SP-X");

        result.IsSuccess.Should().BeTrue();
        var m = result.Value;
        m.Code.Should().Be("SP-X");
        m.FormSchemaJson.Should().NotBeNullOrEmpty();
        m.MandatoryAttachments.Should().NotBeNull();
        m.ReceiptTemplateCode.Should().NotBeNullOrEmpty();
        m.DecisionTemplateCode.Should().NotBeNullOrEmpty();
        m.FisaCalculTemplateCode.Should().NotBeNullOrEmpty();
        m.CalcFormulas.Should().NotBeNull();
        m.ProcessingRulesJson.Should().NotBeNullOrEmpty();
        m.PrintFormTemplateCode.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetMatrixAsync_UnknownCode_ReturnsNotFound()
    {
        using var db = CreateContext();
        var svc = Build(db);

        var result = await svc.GetMatrixAsync("MISSING");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetMatrixAsync_MandatoryAttachmentsPopulated_ReturnsParsedRows()
    {
        using var db = CreateContext();
        const string mandJson = """
            [
                { "documentTypeCode": "ID_CARD", "cardinalityMin": 1, "cardinalityMax": 1 },
                { "documentTypeCode": "PROOF_OF_INSURANCE", "cardinalityMin": 0, "cardinalityMax": 5 }
            ]
            """;
        await SeedPassportAsync(db, "SP-ATT", mandatoryJson: mandJson);

        var svc = Build(db);
        var result = await svc.GetMatrixAsync("SP-ATT");

        result.IsSuccess.Should().BeTrue();
        result.Value.MandatoryAttachments.Should().HaveCount(2);
        result.Value.MandatoryAttachments[0].DocumentTypeCode.Should().Be("ID_CARD");
        result.Value.MandatoryAttachments[0].CardinalityMin.Should().Be(1);
        result.Value.MandatoryAttachments[0].CardinalityMax.Should().Be(1);
        result.Value.MandatoryAttachments[1].DocumentTypeCode.Should().Be("PROOF_OF_INSURANCE");
        result.Value.MandatoryAttachments[1].CardinalityMax.Should().Be(5);
    }

    [Fact]
    public async Task GetMatrixAsync_CalcFormulasPopulated_ReturnsRowsAndEvaluable()
    {
        using var db = CreateContext();
        const string calcJson = """
            [
                { "code": "monthlyBenefit", "formula": "base + bonus * 0.1" },
                { "code": "annualBenefit", "formula": "monthlyBenefit * 12" }
            ]
            """;
        await SeedPassportAsync(db, "SP-CALC", calcJson: calcJson);

        var svc = Build(db);
        var result = await svc.GetMatrixAsync("SP-CALC");

        result.IsSuccess.Should().BeTrue();
        result.Value.CalcFormulas.Should().HaveCount(2);
        result.Value.CalcFormulas[0].Code.Should().Be("monthlyBenefit");
        result.Value.CalcFormulas[0].Formula.Should().Be("base + bonus * 0.1");

        // Sanity: the formulas evaluate via the same evaluator surface.
        var evaluator = new ShuntingYardExpressionEvaluator();
        var monthly = evaluator.Evaluate(result.Value.CalcFormulas[0].Formula,
            new Dictionary<string, decimal> { ["base"] = 1000m, ["bonus"] = 500m });
        monthly.IsSuccess.Should().BeTrue();
        monthly.Value.Should().Be(1050m);
    }

    [Fact]
    public async Task GetMatrixAsync_BlankCode_ReturnsNotFound()
    {
        using var db = CreateContext();
        var svc = Build(db);

        var result = await svc.GetMatrixAsync("");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }
}
