using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.ContributorProfileUpdates;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0362 — controller-level tests for <see cref="ContributorProfileUpdatesController"/>.
/// </summary>
public sealed class ContributorProfileUpdatesControllerTests
{
    /// <summary>Submit returns 201 (<c>CreatedAtAction</c>) with the new request DTO.</summary>
    [Fact]
    public async Task Submit_Success_Returns201WithDto()
    {
        var svc = Substitute.For<IProfileUpdateService>();
        var sqids = Substitute.For<ISqidService>();
        var dto = NewDto();
        svc.SubmitAsync(Arg.Any<ProfileUpdateRequestSubmitDto>(), Arg.Any<CancellationToken>())
           .Returns(Result<ProfileUpdateRequestDto>.Success(dto));
        var controller = new ContributorProfileUpdatesController(svc, sqids);

        var result = await controller.SubmitAsync(
            new ProfileUpdateRequestSubmitDto("abc", "Address", "{}", null),
            CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        created.Value.Should().BeSameAs(dto);
    }

    /// <summary>Approve returns 200 with the resulting (now <c>Applied</c>) DTO.</summary>
    [Fact]
    public async Task Approve_Success_Returns200()
    {
        var svc = Substitute.For<IProfileUpdateService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("k3Gq9").Returns(Result<long>.Success(42));
        var dto = NewDto();
        svc.ApproveAsync(42, Arg.Any<CancellationToken>())
           .Returns(Result<ProfileUpdateRequestDto>.Success(dto));
        var controller = new ContributorProfileUpdatesController(svc, sqids);

        var result = await controller.ApproveAsync("k3Gq9", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>Approve returns 403 when the service reports <c>Forbidden</c>.</summary>
    [Fact]
    public async Task Approve_Forbidden_Returns403()
    {
        var svc = Substitute.For<IProfileUpdateService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("k3Gq9").Returns(Result<long>.Success(42));
        svc.ApproveAsync(42, Arg.Any<CancellationToken>())
           .Returns(Result<ProfileUpdateRequestDto>.Failure(ErrorCodes.Forbidden, "Lacks permission."));
        var controller = new ContributorProfileUpdatesController(svc, sqids);

        var result = await controller.ApproveAsync("k3Gq9", CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    /// <summary>Reject returns 204 on success.</summary>
    [Fact]
    public async Task Reject_Success_Returns204()
    {
        var svc = Substitute.For<IProfileUpdateService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("k3Gq9").Returns(Result<long>.Success(42));
        svc.RejectAsync(42, "reason", Arg.Any<CancellationToken>())
           .Returns(Result.Success());
        var controller = new ContributorProfileUpdatesController(svc, sqids);

        var result = await controller.RejectAsync(
            "k3Gq9",
            new ProfileUpdateRejectRequest("reason"),
            CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    /// <summary>Builds a deterministic DTO for service-result stubs.</summary>
    private static ProfileUpdateRequestDto NewDto() => new(
        Id: "k3Gq9",
        ServiceApplicationSqid: "x1Y2z",
        TargetContributorSqid: "C0nT1",
        Type: "Address",
        Status: "Pending",
        RequestedChangesJson: "{}",
        RejectionReason: null,
        AppliedAtUtc: null,
        ApprovedByUserSqid: null,
        ApplicationErrorJson: null);
}

/// <summary>
/// R0363 — controller-level tests for <see cref="ContributorProfileRefreshController"/>.
/// </summary>
public sealed class ContributorProfileRefreshControllerTests
{
    /// <summary>Refresh returns 200 with the run DTO on success.</summary>
    [Fact]
    public async Task Refresh_Success_Returns200WithDto()
    {
        var svc = Substitute.For<IProfileRefreshService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("C0nT1").Returns(Result<long>.Success(7));
        var dto = NewRunDto();
        svc.RefreshFromSourceAsync("RSP", 7, Arg.Any<CancellationToken>())
           .Returns(Result<ProfileRefreshRunDto>.Success(dto));
        var controller = new ContributorProfileRefreshController(svc, sqids);

        var result = await controller.RefreshAsync("C0nT1", "RSP", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>Unknown source bubbles up as 400.</summary>
    [Fact]
    public async Task Refresh_UnknownSource_Returns400()
    {
        var svc = Substitute.For<IProfileRefreshService>();
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode("C0nT1").Returns(Result<long>.Success(7));
        svc.RefreshFromSourceAsync("FAKE", 7, Arg.Any<CancellationToken>())
           .Returns(Result<ProfileRefreshRunDto>.Failure(
               ErrorCodes.ProfileRefreshUnknownSource, "Unknown source."));
        var controller = new ContributorProfileRefreshController(svc, sqids);

        var result = await controller.RefreshAsync("C0nT1", "FAKE", CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    /// <summary>List endpoint returns the recent run rows verbatim.</summary>
    [Fact]
    public async Task List_Success_ReturnsRows()
    {
        var svc = Substitute.For<IProfileRefreshService>();
        var sqids = Substitute.For<ISqidService>();
        IReadOnlyList<ProfileRefreshRunDto> rows = new[] { NewRunDto() };
        svc.ListRecentAsync(50, Arg.Any<CancellationToken>())
           .Returns(Result<IReadOnlyList<ProfileRefreshRunDto>>.Success(rows));
        var controller = new ContributorProfileRefreshController(svc, sqids);

        var result = await controller.ListRecentAsync(50, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(rows);
    }

    /// <summary>Builds a deterministic run DTO.</summary>
    private static ProfileRefreshRunDto NewRunDto() => new(
        Id: "r1Un",
        Source: "RSP",
        TargetContributorSqid: "C0nT1",
        Outcome: "Success",
        RowsApplied: 2,
        RowsSkipped: 0,
        StartedUtc: new DateTime(2026, 5, 22, 11, 0, 0, DateTimeKind.Utc),
        CompletedUtc: new DateTime(2026, 5, 22, 11, 0, 5, DateTimeKind.Utc),
        FailureSummary: null);
}
