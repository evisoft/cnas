using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Reports;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.Services.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// R0583 / TOR CF 09.06 / CF 09.09 — controller-shape tests for
/// <see cref="ReportJobsController"/>. Direct-construction style mirroring
/// the rest of the controller suite.
/// </summary>
public sealed class ReportJobsControllerTests
{
    /// <summary>Reused stubs for the constructor dependencies.</summary>
    private static (IReportJobService Jobs, ISqidService Sqids) NewMocks()
    {
        var sqids = Substitute.For<ISqidService>();
        sqids.TryDecode(Arg.Any<string?>()).Returns(Result<long>.Success(42L));
        return (Substitute.For<IReportJobService>(), sqids);
    }

    private static ReportJobsController NewController(
        IReportJobService jobs,
        ISqidService sqids) => new(jobs, sqids);

    private static ReportJobDto NewDto(string status = "Queued", string? attachment = null) =>
        new(
            Id: "SQID-1",
            ReportTemplateSqid: "SQID-42",
            RequestedByUserSqid: "SQID-77",
            Format: ExportFormat.Csv.ToString(),
            Status: status,
            QueuedAtUtc: new DateTime(2026, 5, 22, 11, 0, 0, DateTimeKind.Utc),
            StartedAtUtc: null,
            CompletedAtUtc: null,
            AttachmentSqid: attachment,
            FailureReason: null,
            DurationMs: null);

    [Fact]
    public void Controller_IsGatedBy_CnasUser_Policy()
    {
        var attr = typeof(ReportJobsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(AuthorizationComposition.CnasUser);
    }

    [Fact]
    public async Task Enqueue_ServiceSuccess_Returns201_WithDto()
    {
        var (jobs, sqids) = NewMocks();
        var dto = NewDto();
        jobs.EnqueueAsync(Arg.Any<ReportJobEnqueueDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<ReportJobDto>.Success(dto));

        var controller = NewController(jobs, sqids);
        var input = new ReportJobEnqueueDto("SQID-42", ExportFormat.Csv.ToString());
        var result = await controller.EnqueueAsync(input, CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status201Created);
        obj.Value.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task Get_Success_Returns200_WithDto_AttachmentSqidPopulatedOnSucceeded()
    {
        var (jobs, sqids) = NewMocks();
        var dto = NewDto(status: "Succeeded", attachment: "SQID-att-1");
        jobs.GetAsync(42L, Arg.Any<CancellationToken>())
            .Returns(Result<ReportJobDto>.Success(dto));

        var controller = NewController(jobs, sqids);
        var result = await controller.GetAsync("k3Gq9", CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<ReportJobDto>().Subject;
        body.Status.Should().Be("Succeeded");
        body.AttachmentSqid.Should().Be("SQID-att-1");
    }

    [Fact]
    public async Task Cancel_OnSuccess_Returns200()
    {
        var (jobs, sqids) = NewMocks();
        jobs.CancelAsync(42L, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var controller = NewController(jobs, sqids);
        var result = await controller.CancelAsync("k3Gq9", CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Cancel_OnValidationFailure_Returns400_WithJobNotCancellable()
    {
        var (jobs, sqids) = NewMocks();
        jobs.CancelAsync(42L, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorCodes.ValidationFailed, ReportJobService.JobNotCancellableMessage));

        var controller = NewController(jobs, sqids);
        var result = await controller.CancelAsync("k3Gq9", CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // ProblemDetails carries the failure message on its Detail property.
        var details = obj.Value.Should().BeAssignableTo<ProblemDetails>().Subject;
        details.Detail.Should().Be(ReportJobService.JobNotCancellableMessage);
    }
}
