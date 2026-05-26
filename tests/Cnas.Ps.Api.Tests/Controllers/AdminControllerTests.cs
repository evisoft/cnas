using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="AdminController"/>. Direct-construction pattern matching the
/// rest of the controller test suite; the <see cref="IFailedJobStore"/> dependency is faked
/// with NSubstitute. Authorization (<c>CnasTechAdmin</c>) and rate-limiting are validated
/// elsewhere — these tests focus on branch logic.
/// </summary>
public sealed class AdminControllerTests
{
    /// <summary>Deterministic clock value used by sample DTOs.</summary>
    private static readonly DateTime FailedAt = new(2026, 5, 20, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>Helper that returns a fresh store substitute.</summary>
    private static IFailedJobStore NewStoreMock() => Substitute.For<IFailedJobStore>();

    /// <summary>Builds the SUT around the supplied store.</summary>
    private static AdminController NewController(IFailedJobStore store) => new(store);

    /// <summary>Builds a sample paged result with a single Sqid-encoded id.</summary>
    private static PagedResult<FailedJobOutput> SamplePage(int page = 1) =>
        new(
            Items:
            [
                new FailedJobOutput(
                    Id: "k3Gq9",
                    JobName: "mpay-dispatcher",
                    JobGroup: "DEFAULT",
                    FailedAtUtc: FailedAt,
                    ExceptionType: "System.Net.Http.HttpRequestException",
                    ExceptionMessage: "MPay 503",
                    StackTrace: null,
                    RefireCount: 3,
                    ReplayState: null,
                    LastReplayAtUtc: null),
            ],
            Page: page,
            PageSize: 20,
            TotalCount: 1);

    [Fact]
    public async Task ListFailedJobs_Success_Returns200WithPagedBody()
    {
        // Arrange
        var store = NewStoreMock();
        var paged = SamplePage();
        store.QueryAsync(
                Arg.Any<string?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<PageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<FailedJobOutput>>.Success(paged));
        var controller = NewController(store);

        // Act — no filters; defaults applied at the action signature.
        var result = await controller.ListFailedJobsAsync(
            jobName: null, since: null, page: 1, pageSize: 20, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(paged);
    }

    [Fact]
    public async Task ListFailedJobs_AppliesJobNameFilterToStore()
    {
        // Arrange — assert that the route query parameters reach the store verbatim.
        var store = NewStoreMock();
        store.QueryAsync(
                Arg.Any<string?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<PageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<FailedJobOutput>>.Success(SamplePage(page: 2)));
        var controller = NewController(store);

        // Act
        var since = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        _ = await controller.ListFailedJobsAsync(
            jobName: "mpay-dispatcher", since: since, page: 2, pageSize: 50, CancellationToken.None);

        // Assert — verify the store saw the same filters we passed in.
        await store.Received(1).QueryAsync(
            Arg.Is<string?>(s => s == "mpay-dispatcher"),
            Arg.Is<DateTime?>(d => d == since),
            Arg.Is<PageRequest>(p => p.Page == 2 && p.PageSize == 50),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListFailedJobs_StoreFailure_Returns400()
    {
        // Arrange — generic failure path.
        var store = NewStoreMock();
        store.QueryAsync(
                Arg.Any<string?>(),
                Arg.Any<DateTime?>(),
                Arg.Any<PageRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<PagedResult<FailedJobOutput>>.Failure(
                ErrorCodes.Internal, "Database unavailable."));
        var controller = NewController(store);

        // Act
        var result = await controller.ListFailedJobsAsync(
            jobName: null, since: null, page: 1, pageSize: 20, CancellationToken.None);

        // Assert
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ReplayFailedJob_Success_Returns204()
    {
        // Arrange
        var store = NewStoreMock();
        store.ReplayAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Result.Success());
        var controller = NewController(store);

        // Act
        var result = await controller.ReplayFailedJobAsync("k3Gq9", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        await store.Received(1).ReplayAsync(
            Arg.Is<string>(s => s == "k3Gq9"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplayFailedJob_NotFound_Returns404()
    {
        // Arrange — store reports the DLQ id does not exist.
        var store = NewStoreMock();
        store.ReplayAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Result.Failure(ErrorCodes.NotFound, "FailedJob entry not found."));
        var controller = NewController(store);

        // Act
        var result = await controller.ReplayFailedJobAsync("MISSING", CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ReplayFailedJob_InvalidSqid_Returns400()
    {
        // Arrange — store reports a malformed Sqid via the standard error code.
        var store = NewStoreMock();
        store.ReplayAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Result.Failure(ErrorCodes.InvalidSqid, "Sqid could not be decoded."));
        var controller = NewController(store);

        // Act
        var result = await controller.ReplayFailedJobAsync("!!!", CancellationToken.None);

        // Assert
        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Value.Should().BeAssignableTo<ProblemDetails>()
               .Which.Detail.Should().Be("Sqid could not be decoded.");
    }
}
