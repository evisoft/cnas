using Cnas.Ps.Api.Composition;
using Cnas.Ps.Api.Controllers.Interop;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Claim = System.Security.Claims.Claim;
using ClaimsIdentity = System.Security.Claims.ClaimsIdentity;
using ClaimsPrincipal = System.Security.Claims.ClaimsPrincipal;
using ClaimTypes = System.Security.Claims.ClaimTypes;

namespace Cnas.Ps.Api.Tests.Controllers.Interop;

/// <summary>
/// R2161 / TOR INT 002 — controller-level unit tests for
/// <see cref="OfflineBatchJobsController"/>. Covers the happy-path ingest /
/// export / status routes plus the structural policy-gate assertion (the
/// no-perm 403 is enforced declaratively via <c>[Authorize(Policy=CnasUser)]</c>;
/// the test asserts the attribute is in place so a drive-by edit cannot
/// relax it).
/// </summary>
public sealed class OfflineBatchJobsControllerTests
{
    /// <summary>Three-row ingest payload reused by the happy-path test.</summary>
    private static readonly string[] ThreeRowPayload = { "r1", "r2", "r3" };

    /// <summary>Single-filter export payload reused by the happy-path test.</summary>
    private static readonly string[] SingleFilterPayload = { "f1" };

    private static OfflineBatchJobsController NewController(IOfflineBatchService service)
    {
        var ctrl = new OfflineBatchJobsController(service);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
        }, "TestAuth");
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
            },
        };
        return ctrl;
    }

    private static OfflineBatchJobDto NewDto(string status = "Pending", string id = "SQID-1")
        => new(
            Id: id,
            Kind: nameof(OfflineBatchJobKind.Ingest),
            Status: status,
            SubmittedAtUtc: new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc),
            StartedAtUtc: null,
            CompletedAtUtc: null,
            ErrorMessage: null,
            ResultBlobKey: null,
            RowCount: 3);

    /// <summary>
    /// R2161 — controller carries the <c>[Authorize(Policy=CnasUser)]</c>
    /// attribute so anonymous (401) + non-CnasUser (403) traffic is rejected
    /// by the auth middleware before any controller code runs.
    /// </summary>
    [Fact]
    public void Controller_GatedBy_CnasUserPolicy()
    {
        var attr = typeof(OfflineBatchJobsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();
        attr.Should().NotBeNull();
        attr!.Policy.Should().Be(AuthorizationComposition.CnasUser);
    }

    /// <summary>R2161 — POST /ingest returns 201 with a Location header.</summary>
    [Fact]
    public async Task SubmitIngest_Success_Returns201Created()
    {
        var svc = Substitute.For<IOfflineBatchService>();
        svc.SubmitIngestAsync(Arg.Any<OfflineBatchIngestInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchJobDto>.Success(NewDto()));

        var ctrl = NewController(svc);
        var body = new OfflineBatchIngestInputDto(Description: "d", Rows: ThreeRowPayload);

        var result = await ctrl.SubmitIngestAsync(body, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    /// <summary>R2161 — POST /export returns 201 with a Location header.</summary>
    [Fact]
    public async Task SubmitExport_Success_Returns201Created()
    {
        var svc = Substitute.For<IOfflineBatchService>();
        svc.SubmitExportAsync(Arg.Any<OfflineBatchExportInputDto>(), Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchJobDto>.Success(NewDto(status: nameof(OfflineBatchJobStatus.Pending))));

        var ctrl = NewController(svc);
        var body = new OfflineBatchExportInputDto(Description: "d", Filters: SingleFilterPayload);

        var result = await ctrl.SubmitExportAsync(body, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    /// <summary>R2161 — GET /{sqid} returns 200 for the owner's job.</summary>
    [Fact]
    public async Task GetStatus_OwnedJob_Returns200()
    {
        var svc = Substitute.For<IOfflineBatchService>();
        svc.GetStatusAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchJobDto>.Success(NewDto()));

        var ctrl = NewController(svc);
        var result = await ctrl.GetStatusAsync("SQID-1", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>R2161 — GET /{sqid} returns 404 when the service surfaces NotFound (cross-user / missing row).</summary>
    [Fact]
    public async Task GetStatus_NotFound_Returns404()
    {
        var svc = Substitute.For<IOfflineBatchService>();
        svc.GetStatusAsync("SQID-9", Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchJobDto>.Failure(ErrorCodes.NotFound, "missing"));

        var ctrl = NewController(svc);
        var result = await ctrl.GetStatusAsync("SQID-9", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }
}
