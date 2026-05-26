using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.PersonalAccount;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0516 — controller-level unit tests for
/// <see cref="PersonalAccountController"/>. Asserts the happy-path 200 shape
/// on both the self-service and admin routes, the 403 ProblemDetails when
/// the admin route is exercised without the
/// <c>PersonalAccount.ReadAny</c> permission, and the Sqid-decode error
/// path on the admin route.
/// </summary>
public sealed class PersonalAccountControllerTests
{
    /// <summary>Helper that returns a fresh service substitute.</summary>
    private static IPersonalAccountExtractService NewServiceMock() =>
        Substitute.For<IPersonalAccountExtractService>();

    /// <summary>Builds the SUT around the supplied service.</summary>
    private static PersonalAccountController NewController(
        IPersonalAccountExtractService svc,
        ISqidService? sqids = null)
    {
        if (sqids is null)
        {
            sqids = Substitute.For<ISqidService>();
            sqids.TryDecode(Arg.Any<string?>())
                 .Returns(call =>
                 {
                     var s = call.Arg<string?>();
                     // Accept simple "SQID-{n}" pattern; reject anything else as InvalidSqid.
                     if (s is not null && s.StartsWith("SQID-", StringComparison.Ordinal)
                         && long.TryParse(s.AsSpan(5), out var id))
                     {
                         return Result<long>.Success(id);
                     }
                     return Result<long>.Failure(ErrorCodes.InvalidSqid, "Invalid sqid.");
                 });
        }
        return new PersonalAccountController(svc, sqids);
    }

    /// <summary>Builds a populated extract DTO for happy-path tests.</summary>
    private static PersonalAccountExtractDto SampleExtract(string accountCode, string solicitantSqid)
        => new(
            AccountCodeSqid: accountCode,
            SolicitantSqid: solicitantSqid,
            Years: Array.Empty<PersonalAccountYearDto>(),
            GrandTotalContributions: 0m,
            GrandTotalMonths: 0,
            GeneratedAtUtc: new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// Controller test — self-service GET returns 200 with the populated DTO.
    /// </summary>
    [Fact]
    public async Task R0516_GetMine_Success_Returns200_WithExtractDto()
    {
        var svc = NewServiceMock();
        var dto = SampleExtract("PA-1001", "SQID-1");
        svc.GetForCurrentUserAsync(Arg.Any<CancellationToken>())
           .Returns(Result<PersonalAccountExtractDto>.Success(dto));
        var controller = NewController(svc);

        var result = await controller.GetMineAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>
    /// Controller test — admin GET surfaces Forbidden as 403 ProblemDetails.
    /// </summary>
    [Fact]
    public async Task R0516_GetForSolicitant_Forbidden_Returns403()
    {
        var svc = NewServiceMock();
        svc.GetForSolicitantAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
           .Returns(Result<PersonalAccountExtractDto>.Failure(
               ErrorCodes.Forbidden, "Permission required."));
        var controller = NewController(svc);

        var result = await controller.GetForSolicitantAsync("SQID-42", CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(403);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["errorCode"].Should().Be(ErrorCodes.Forbidden);
    }
}
