using Cnas.Ps.Api.Controllers.Interop;
using Cnas.Ps.Application.Interop.Batch;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Claim = System.Security.Claims.Claim;
using ClaimsIdentity = System.Security.Claims.ClaimsIdentity;
using ClaimsPrincipal = System.Security.Claims.ClaimsPrincipal;
using ClaimTypes = System.Security.Claims.ClaimTypes;

namespace Cnas.Ps.Api.Tests.Controllers.Interop;

/// <summary>
/// R1710 / TOR INT 002 — controller-level unit tests for
/// <see cref="OfflineBatchController"/>. Direct construction with a faked
/// <see cref="IOfflineBatchSubmissionService"/>; the controller is the
/// boundary that enforces ConsumerSubject = authenticated subject.
/// </summary>
public sealed class OfflineBatchControllerTests
{
    private static OfflineBatchController NewController(
        IOfflineBatchSubmissionService service,
        string subject)
    {
        var ctrl = new OfflineBatchController(service);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, subject),
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

    private static OfflineBatchSubmissionDto NewDto(
        string subject,
        string status = "Queued",
        string id = "SQID-1")
        => new(
            Id: id,
            BatchNumber: "OBS-2026-000001",
            ConsumerSubject: subject,
            OpCode: nameof(AnnexFourBatchOp.GetInsuredPersonStatus),
            Status: status,
            RequestFileName: "req.csv",
            RequestFileSizeBytes: 100,
            RequestFileHashSha256: new string('a', 64),
            RequestRowCount: 2,
            ResponseFileHashSha256: status == "Completed" ? new string('b', 64) : null,
            ResponseFileSignatureBase64: status == "Completed" ? "sig==" : null,
            SubmittedAt: new DateTime(2026, 5, 23, 4, 0, 0, DateTimeKind.Utc),
            StartedAt: null,
            CompletedAt: status == "Completed" ? new DateTime(2026, 5, 23, 5, 0, 0, DateTimeKind.Utc) : null,
            FailureReason: null,
            TotalRowsProcessed: status == "Completed" ? 2 : 0,
            TotalRowsFailed: 0);

    /// <summary>R1710 — POST submit overwrites ConsumerSubject with the auth subject.</summary>
    [Fact]
    public async Task Submit_ServerFillsConsumerSubject_From_AuthClaim()
    {
        var svc = Substitute.For<IOfflineBatchSubmissionService>();
        OfflineBatchSubmissionInputDto? captured = null;
        svc.SubmitAsync(Arg.Do<OfflineBatchSubmissionInputDto>(i => captured = i), Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchSubmissionDto>.Success(NewDto("client-real")));

        var ctrl = NewController(svc, "client-real");
        var body = new OfflineBatchSubmissionInputDto(
            ConsumerSubject: "client-spoofed",
            OpCode: nameof(AnnexFourBatchOp.GetInsuredPersonStatus),
            RequestFileName: "req.csv",
            RequestFileBytes: new byte[] { 1 },
            RequestFileHashSha256: new string('a', 64));

        var result = await ctrl.SubmitAsync(body, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        captured!.ConsumerSubject.Should().Be("client-real");
    }

    /// <summary>R1710 — GET returns 200 for the caller's own submission.</summary>
    [Fact]
    public async Task GetById_OwnedSubmission_Returns200()
    {
        var svc = Substitute.For<IOfflineBatchSubmissionService>();
        svc.GetByIdAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchSubmissionDto>.Success(NewDto("client-real")));

        var ctrl = NewController(svc, "client-real");
        var result = await ctrl.GetByIdAsync("SQID-1", CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    /// <summary>R1710 — GET returns 404 when the submission belongs to a different subject.</summary>
    [Fact]
    public async Task GetById_CrossSubjectLookup_Returns404()
    {
        var svc = Substitute.For<IOfflineBatchSubmissionService>();
        svc.GetByIdAsync("SQID-9", Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchSubmissionDto>.Success(NewDto("client-other")));

        var ctrl = NewController(svc, "client-real");
        var result = await ctrl.GetByIdAsync("SQID-9", CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    /// <summary>R1710 — download endpoint streams CSV with hash + signature headers when Completed.</summary>
    [Fact]
    public async Task Download_OnCompleted_ReturnsCsvWithIntegrityHeaders()
    {
        var svc = Substitute.For<IOfflineBatchSubmissionService>();
        svc.GetByIdAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchSubmissionDto>.Success(NewDto("client-real", "Completed")));
        var info = new OfflineBatchDownloadInfoDto(
            DownloadUrl: "/api/interop/batch/submissions/SQID-1/download",
            FileName: "OBS-2026-000001-response.csv",
            ContentType: "text/csv",
            SizeBytes: 5,
            HashSha256: new string('b', 64),
            SignatureBase64: "sig==",
            SignedAt: new DateTime(2026, 5, 23, 5, 0, 0, DateTimeKind.Utc));
        svc.GetDownloadBytesAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchDownloadBytesDto>.Success(
                new OfflineBatchDownloadBytesDto(info, new byte[] { 1, 2, 3, 4, 5 })));

        var ctrl = NewController(svc, "client-real");
        var result = await ctrl.DownloadAsync("SQID-1", CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("text/csv; charset=utf-8");
        var headers = ctrl.Response.Headers;
        headers["X-Batch-Hash-Sha256"].ToString().Should().Be(new string('b', 64));
        headers["X-Batch-Signature-Hmac"].ToString().Should().Be("sig==");
    }

    /// <summary>R1710 — download on non-Completed submission returns 409.</summary>
    [Fact]
    public async Task Download_OnNonCompleted_Returns409()
    {
        var svc = Substitute.For<IOfflineBatchSubmissionService>();
        svc.GetByIdAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchSubmissionDto>.Success(NewDto("client-real", "Queued")));
        svc.GetDownloadBytesAsync("SQID-1", Arg.Any<CancellationToken>())
            .Returns(Result<OfflineBatchDownloadBytesDto>.Failure(ErrorCodes.Conflict, "not ready"));

        var ctrl = NewController(svc, "client-real");
        var result = await ctrl.DownloadAsync("SQID-1", CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }
}
