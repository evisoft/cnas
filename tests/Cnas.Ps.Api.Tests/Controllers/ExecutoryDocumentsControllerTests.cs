using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.ExecutoryDocuments;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R1600 / R1406 — controller-level tests for
/// <see cref="ExecutoryDocumentsController"/>.
/// </summary>
public sealed class ExecutoryDocumentsControllerTests
{
    /// <summary>Builds a canonical register input.</summary>
    private static ExecutoryDocumentRegisterInputDto SampleRegisterInput() => new(
        DocumentSeriesNumber: null,
        DebtorIdnp: "2002000000007",
        Kind: nameof(ExecutoryDocumentKind.CourtOrder),
        IssuedBy: "Judecătoria Chișinău",
        IssuedDate: new DateOnly(2026, 5, 1),
        EffectiveFrom: new DateOnly(2026, 5, 15),
        EffectiveUntil: null,
        WithholdingMode: nameof(ExecutoryDocumentWithholdingMode.FixedAmount),
        WithholdingAmountMdl: 1_000m,
        WithholdingPercentage: null,
        PriorityRank: 1,
        CreditorAccountIban: "MD24AG000225100013104168",
        CreditorName: "Direcția Asistență Socială",
        TotalOwedMdl: 5_000m);

    /// <summary>Builds a canonical output DTO returned by the service mock.</summary>
    private static ExecutoryDocumentDto SampleDto() => new(
        Id: "EXE-1",
        DocumentSeriesNumber: "EXE-2026-000001",
        DebtorIdnp: "2002000000007",
        Kind: nameof(ExecutoryDocumentKind.CourtOrder),
        Status: nameof(ExecutoryDocumentStatus.Active),
        IssuedBy: "Judecătoria Chișinău",
        IssuedDate: new DateOnly(2026, 5, 1),
        EffectiveFrom: new DateOnly(2026, 5, 15),
        EffectiveUntil: null,
        WithholdingMode: nameof(ExecutoryDocumentWithholdingMode.FixedAmount),
        WithholdingAmountMdl: 1_000m,
        WithholdingPercentage: null,
        PriorityRank: 1,
        CreditorAccountIban: "MD24AG000225100013104168",
        CreditorName: "Direcția Asistență Socială",
        TotalOwedMdl: 5_000m,
        TotalWithheldMdl: 0m,
        CompletedDate: null,
        CancellationReason: null);

    /// <summary>R1600 — POST /api/executory-documents returns 201 on success.</summary>
    [Fact]
    public async Task RegisterAsync_ServiceReturnsSuccess_Returns201()
    {
        var svc = Substitute.For<IExecutoryDocumentService>();
        svc.RegisterAsync(Arg.Any<ExecutoryDocumentRegisterInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<ExecutoryDocumentDto>.Success(SampleDto()));
        var controller = new ExecutoryDocumentsController(svc);

        var result = await controller.RegisterAsync(SampleRegisterInput(), CancellationToken.None);

        var created = result.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        var dto = created.Value.Should().BeOfType<ExecutoryDocumentDto>().Subject;
        dto.Id.Should().Be("EXE-1");
        dto.Status.Should().Be(nameof(ExecutoryDocumentStatus.Active));
    }

    /// <summary>R1600 — PUT /api/executory-documents/{sqid} returns 200 on success.</summary>
    [Fact]
    public async Task ModifyAsync_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IExecutoryDocumentService>();
        svc.ModifyAsync(Arg.Any<string>(), Arg.Any<ExecutoryDocumentModifyInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<ExecutoryDocumentDto>.Success(SampleDto()));
        var controller = new ExecutoryDocumentsController(svc);

        var input = new ExecutoryDocumentModifyInputDto(
            IssuedBy: null,
            EffectiveUntil: null,
            WithholdingMode: null,
            WithholdingAmountMdl: 2_000m,
            WithholdingPercentage: null,
            PriorityRank: null,
            CreditorAccountIban: null,
            CreditorName: null,
            TotalOwedMdl: null,
            ChangeReason: "Court re-evaluated amount");

        var result = await controller.ModifyAsync("EXE-1", input, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>R1600 — POST /api/executory-documents/{sqid}/cancel returns 200 on success.</summary>
    [Fact]
    public async Task CancelAsync_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IExecutoryDocumentService>();
        svc.CancelAsync(Arg.Any<string>(), Arg.Any<ExecutoryDocumentReasonInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<ExecutoryDocumentDto>.Success(SampleDto() with { Status = nameof(ExecutoryDocumentStatus.Cancelled), CancellationReason = "test" }));
        var controller = new ExecutoryDocumentsController(svc);

        var result = await controller.CancelAsync(
            "EXE-1",
            new ExecutoryDocumentReasonInputDto("Court reversed"),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>R1600 — GET /api/executory-documents/{sqid} returns 200 when found.</summary>
    [Fact]
    public async Task GetAsync_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<IExecutoryDocumentService>();
        svc.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<ExecutoryDocumentDto>.Success(SampleDto()));
        var controller = new ExecutoryDocumentsController(svc);

        var result = await controller.GetAsync("EXE-1", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        var dto = ok.Value.Should().BeOfType<ExecutoryDocumentDto>().Subject;
        dto.Id.Should().Be("EXE-1");
    }
}
