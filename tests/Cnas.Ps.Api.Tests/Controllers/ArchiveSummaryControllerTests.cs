using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Archive;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0332 / TOR CF 12.02 — direct-construction tests for
/// <see cref="ArchiveSummaryController"/>. Mirrors the
/// <c>ContributorsControllerTests</c> pattern — no full HTTP pipeline boot,
/// just route-handler branch coverage with a mocked <see cref="IArchiveMetadataService"/>.
/// </summary>
public sealed class ArchiveSummaryControllerTests
{
    /// <summary>Sample summary used by the success-path test.</summary>
    private static ArchiveSummaryDto SampleSummary() =>
        new(
            Contributors: new ArchiveTabSummaryDto("contributors", 5, 1, new DateTime(2026, 5, 24, 10, 0, 0, DateTimeKind.Utc)),
            InsuredPersons: new ArchiveTabSummaryDto("insured-persons", 10, 0, new DateTime(2026, 5, 24, 9, 0, 0, DateTimeKind.Utc)),
            Decisions: new ArchiveTabSummaryDto("decisions", 2, 0, null),
            Dossiers: new ArchiveTabSummaryDto("dossiers", 3, 1, null),
            Documents: new ArchiveTabSummaryDto("documents", 7, 2, null));

    /// <summary>
    /// Happy path — the service returns a populated summary and the
    /// controller surfaces it inside <see cref="OkObjectResult"/> with the
    /// payload intact (no projection / mutation in the controller).
    /// </summary>
    [Fact]
    public async Task GetSummary_ServiceReturnsSuccess_Returns200WithDto()
    {
        var svc = Substitute.For<IArchiveMetadataService>();
        var dto = SampleSummary();
        svc.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(Result<ArchiveSummaryDto>.Success(dto));
        var controller = new ArchiveSummaryController(svc);

        var actionResult = await controller.GetSummaryAsync(CancellationToken.None);

        var ok = actionResult.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(dto);
    }

    /// <summary>
    /// Failure path — the service returns a transport failure and the
    /// controller surfaces it as HTTP 500 ProblemDetails so the front end
    /// can render the error alert without parsing the body.
    /// </summary>
    [Fact]
    public async Task GetSummary_ServiceReturnsFailure_Returns500()
    {
        var svc = Substitute.For<IArchiveMetadataService>();
        svc.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(Result<ArchiveSummaryDto>.Failure(ErrorCodes.Internal, "replica down"));
        var controller = new ArchiveSummaryController(svc);

        var actionResult = await controller.GetSummaryAsync(CancellationToken.None);

        var problem = actionResult.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    /// <summary>
    /// Cancellation token — the controller forwards the same token through
    /// to <see cref="IArchiveMetadataService.GetSummaryAsync"/> so callers
    /// who drop the request see the upstream call cancel too.
    /// </summary>
    [Fact]
    public async Task GetSummary_ForwardsCancellationToken()
    {
        var svc = Substitute.For<IArchiveMetadataService>();
        svc.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(Result<ArchiveSummaryDto>.Success(SampleSummary()));
        var controller = new ArchiveSummaryController(svc);
        using var cts = new CancellationTokenSource();

        await controller.GetSummaryAsync(cts.Token);

        await svc.Received(1).GetSummaryAsync(cts.Token);
    }
}
