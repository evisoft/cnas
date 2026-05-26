using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Prefill;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0552 / R0562 — controller-level unit tests for <see cref="PrefillController"/>.
/// Asserts the happy-path 200 shape on both routes, the 403 ProblemDetails when the
/// staff route is hit without permission, and the Sqid-decode error path.
/// </summary>
public sealed class PrefillControllerTests
{
    private static IPrefillService NewServiceMock() => Substitute.For<IPrefillService>();

    private static PrefillController NewController(IPrefillService svc, ISqidService? sqids = null)
    {
        if (sqids is null)
        {
            sqids = Substitute.For<ISqidService>();
            sqids.TryDecode(Arg.Any<string?>())
                 .Returns(call =>
                 {
                     var s = call.Arg<string?>();
                     if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                         && long.TryParse(s.AsSpan(5), out var id))
                     {
                         return Result<long>.Success(id);
                     }
                     return Result<long>.Failure(ErrorCodes.InvalidSqid, "Invalid sqid.");
                 });
        }
        return new PrefillController(svc, sqids);
    }

    private static PrefillPayloadDto SamplePayload(string solicitantSqid) =>
        new(
            SolicitantSqid: solicitantSqid,
            Fields: new Dictionary<string, PrefillFieldDto>(StringComparer.Ordinal)
            {
                [PrefillFields.FullName] = new("ANA POPESCU", PrefillSources.Rsp, DateTime.UtcNow),
            },
            Warnings: Array.Empty<string>(),
            GeneratedAtUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
            SourceUsedPerField: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrefillFields.FullName] = PrefillSources.Rsp,
            });

    /// <summary>15. POST /api/self-service/prefill returns 200 with the populated DTO.</summary>
    [Fact]
    public async Task R0552_PostSelfServicePrefill_Success_Returns200WithDto()
    {
        var svc = NewServiceMock();
        var payload = SamplePayload("SQID-1");
        svc.PrefillForCurrentUserAsync(Arg.Any<PrefillRequestDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<PrefillPayloadDto>.Success(payload));
        var controller = NewController(svc);

        var result = await controller.PrefillForCurrentUserAsync(
            new PrefillRequestDto(null, null), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(payload);
    }

    /// <summary>R0562 — admin POST surfaces Forbidden as 403 ProblemDetails.</summary>
    [Fact]
    public async Task R0562_PostAdminPrefill_Forbidden_Returns403()
    {
        var svc = NewServiceMock();
        svc.PrefillForSolicitantAsync(Arg.Any<long>(), Arg.Any<PrefillRequestDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<PrefillPayloadDto>.Failure(ErrorCodes.Forbidden, "Permission required."));
        var controller = NewController(svc);

        var result = await controller.PrefillForSolicitantAsync(
            "SQID-7", new PrefillRequestDto(null, null), CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(403);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["errorCode"].Should().Be(ErrorCodes.Forbidden);
    }

    /// <summary>R0562 — invalid Sqid on the admin route surfaces 400.</summary>
    [Fact]
    public async Task R0562_PostAdminPrefill_InvalidSqid_Returns400()
    {
        var svc = NewServiceMock();
        var controller = NewController(svc);

        var result = await controller.PrefillForSolicitantAsync(
            "not-a-sqid", new PrefillRequestDto(null, null), CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
    }
}
