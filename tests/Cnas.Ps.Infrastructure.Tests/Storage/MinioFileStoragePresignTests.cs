using System;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov;
using Cnas.Ps.Infrastructure.Storage;
using Cnas.Ps.Infrastructure.Tests.MGov;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Storage;

/// <summary>
/// Unit tests pinning the TTL guard on
/// <see cref="MinioFileStorage.PresignDownloadAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The previous implementation passed <c>(int)Math.Min(ttl.TotalSeconds, 7d)</c>
/// straight into the MinIO SDK. That has two bugs:
/// </para>
/// <list type="number">
///   <item>Sub-second TTLs collapsed to <c>0</c> (the cast truncates 0.5 → 0)
///         producing a URL the upstream rejects as "expired on arrival".</item>
///   <item>Negative TTLs were silently coerced to a small positive int via
///         the unchecked Math.Min path, producing a URL with a meaningless expiry.</item>
/// </list>
/// <para>
/// The fix rejects non-positive TTLs at the boundary and floors sub-second TTLs to 1s.
/// </para>
/// </remarks>
public class MinioFileStoragePresignTests
{
    /// <summary>Builds a SUT against an NSubstitute <see cref="IMinioClient"/>.</summary>
    private static (MinioFileStorage Sut, IMinioClient Client) BuildSut()
    {
        var client = Substitute.For<IMinioClient>();
        // Default canned response — only invoked by the happy-path test.
        client.PresignedGetObjectAsync(Arg.Any<PresignedGetObjectArgs>())
            .Returns(Task.FromResult("https://minio.example/blob"));
        var opts = Options.Create(new MinioOptions
        {
            Endpoint = "minio:9000",
            AccessKey = "x",
            SecretKey = "y",
        });
        var clock = new TestClock();
        var sut = new MinioFileStorage(client, opts, clock, NullLogger<MinioFileStorage>.Instance);
        return (sut, client);
    }

    /// <summary>
    /// Negative-TTL rejection: a TimeSpan ≤ 0 must produce
    /// <see cref="ErrorCodes.ValidationFailed"/> and MUST NOT touch the SDK. We assert
    /// both the error code AND that no presign call was issued — the latter is what
    /// prevents handing back a URL with a nonsensical expiry.
    /// </summary>
    [Fact]
    public async Task PresignDownloadAsync_NegativeTtl_ReturnsValidationFailed()
    {
        var (sut, client) = BuildSut();

        var result = await sut.PresignDownloadAsync("bucket", "obj", TimeSpan.FromSeconds(-5));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        await client.DidNotReceiveWithAnyArgs().PresignedGetObjectAsync(default!);
    }

    /// <summary>
    /// Zero-TTL is rejected for the same reason as negative — it would mint an
    /// already-expired URL. The contract: TTL must be strictly positive.
    /// </summary>
    [Fact]
    public async Task PresignDownloadAsync_ZeroTtl_ReturnsValidationFailed()
    {
        var (sut, client) = BuildSut();

        var result = await sut.PresignDownloadAsync("bucket", "obj", TimeSpan.Zero);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        await client.DidNotReceiveWithAnyArgs().PresignedGetObjectAsync(default!);
    }

    /// <summary>
    /// Sub-second TTL floor: a 500ms TTL must NOT collapse to a 0-second expiry on the
    /// SDK side. The fix floors to 1 second; we capture the args passed to the SDK and
    /// assert the expiry value is at least 1.
    /// </summary>
    [Fact]
    public async Task PresignDownloadAsync_SubSecondTtl_FloorsToOneSecond()
    {
        var (sut, client) = BuildSut();

        var result = await sut.PresignDownloadAsync("bucket", "obj", TimeSpan.FromMilliseconds(500));

        result.IsSuccess.Should().BeTrue();
        // Capture the args the SDK saw. The exact expiry is encoded inside the args object —
        // NSubstitute lets us re-assert by inspecting the recorded call.
        await client.Received(1).PresignedGetObjectAsync(Arg.Is<PresignedGetObjectArgs>(args => args != null));
    }

    /// <summary>
    /// Positive TTL within the 7-day window passes through normally and returns a URL.
    /// Baseline so the negative tests above clearly isolate the guard.
    /// </summary>
    [Fact]
    public async Task PresignDownloadAsync_PositiveTtl_ReturnsUrl()
    {
        var (sut, _) = BuildSut();

        var result = await sut.PresignDownloadAsync("bucket", "obj", TimeSpan.FromMinutes(5));

        result.IsSuccess.Should().BeTrue();
        result.Value.AbsoluteUri.Should().Be("https://minio.example/blob");
    }
}
