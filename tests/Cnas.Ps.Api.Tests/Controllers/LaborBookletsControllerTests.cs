using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.LaborBooklet;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0920 / R0921 — controller-level tests for
/// <see cref="LaborBookletsController"/>. Verifies the success/failure routing
/// for the most common endpoint shapes.
/// </summary>
public sealed class LaborBookletsControllerTests
{
    /// <summary>Default DTO returned by the service mock.</summary>
    private static LaborBookletDto SampleOutput(string status = "Pending") => new(
        Id: "LB-1",
        InsuredPersonSqid: "SQID-1",
        CarnetMuncaNumber: "AB-100",
        IssuedDate: new DateOnly(1990, 1, 1),
        IssuingAuthority: "Cooperativa A",
        Status: status,
        OcrConfidenceLevel: null,
        VerifierNotes: null,
        VerifiedByUserSqid: null,
        VerifiedAtUtc: null,
        RejectionReason: null,
        RejectedAtUtc: null,
        HasScannedCopy: false);

    /// <summary>Sqid stub that round-trips "SQID-{id}" / "LB-{id}" forms.</summary>
    private static ISqidService NewSqidStub()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.Encode(Arg.Any<long>()).Returns(call => $"SQID-{call.Arg<long>()}");
        sqids.TryDecode(Arg.Any<string>()).Returns(call =>
        {
            var s = call.Arg<string>();
            if (s is not null)
            {
                if (s.StartsWith("SQID-", StringComparison.Ordinal)
                    && long.TryParse(s["SQID-".Length..], out var id1))
                {
                    return Result<long>.Success(id1);
                }
                if (s.StartsWith("LB-", StringComparison.Ordinal)
                    && long.TryParse(s["LB-".Length..], out var id2))
                {
                    return Result<long>.Success(id2);
                }
            }
            return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
        });
        return sqids;
    }

    /// <summary>R0920 — POST /api/labor-booklets returns 201 with the Sqid id on success.</summary>
    [Fact]
    public async Task Register_ServiceReturnsSuccess_Returns201WithSqid()
    {
        var svc = Substitute.For<ILaborBookletService>();
        svc.RegisterAsync(Arg.Any<LaborBookletRegisterInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<LaborBookletDto>.Success(SampleOutput()));
        var controller = new LaborBookletsController(svc, NewSqidStub());

        var result = await controller.RegisterAsync(
            new LaborBookletRegisterInputDto(
                InsuredPersonSqid: "SQID-1",
                CarnetMuncaNumber: "AB-100"),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<LaborBookletDto>().Subject;
        dto.Id.Should().Be("LB-1");
        dto.Status.Should().Be("Pending");
    }

    /// <summary>R0920 — POST /api/labor-booklets/{sqid}/verify returns 200 with updated DTO.</summary>
    [Fact]
    public async Task Verify_ServiceReturnsSuccess_Returns200()
    {
        var svc = Substitute.For<ILaborBookletService>();
        svc.VerifyAsync(Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<LaborBookletDto>.Success(SampleOutput(status: "Verified")));
        var controller = new LaborBookletsController(svc, NewSqidStub());

        var result = await controller.VerifyAsync(
            "LB-1",
            new LaborBookletVerifyInputDto(Notes: null),
            CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<LaborBookletDto>().Subject;
        dto.Status.Should().Be("Verified");
    }

    /// <summary>R0920 — Conflict from the service surfaces as 409 ProblemDetails.</summary>
    [Fact]
    public async Task Register_ServiceReturnsConflict_Returns409()
    {
        var svc = Substitute.For<ILaborBookletService>();
        svc.RegisterAsync(Arg.Any<LaborBookletRegisterInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<LaborBookletDto>.Failure(ErrorCodes.Conflict, "LABOR_BOOKLET_DUPLICATE"));
        var controller = new LaborBookletsController(svc, NewSqidStub());

        var result = await controller.RegisterAsync(
            new LaborBookletRegisterInputDto(InsuredPersonSqid: "SQID-1", CarnetMuncaNumber: "AB-100"),
            CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(409);
    }
}
