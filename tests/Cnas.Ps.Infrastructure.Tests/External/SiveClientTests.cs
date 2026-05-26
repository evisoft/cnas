using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.External;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Infrastructure.MGov.External;
using Cnas.Ps.Infrastructure.Tests.MGov;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.External;

/// <summary>Unit tests for <see cref="SiveClient"/> — the SIVE energy-vulnerability facade.</summary>
public class SiveClientTests
{
    private const string ValidIdnp = "2000123456782";

    private static (SiveClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new SiveClient(mconnect, new TestClock(), NullLogger<SiveClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetVulnerabilityAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetVulnerabilityAsync(string.Empty);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetVulnerabilityAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload =
            "{\"isVulnerable\":true,\"certifiedOn\":\"2025-10-01\",\"expiresOn\":\"2026-04-01\",\"category\":\"HIGH\"}";
        mconnect.CallAsync("SIVE.GetEnergyVulnerability", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetVulnerabilityAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.IsVulnerable.Should().BeTrue();
        result.Value.Category.Should().Be("HIGH");
    }

    [Fact]
    public async Task GetVulnerabilityAsync_NotFoundResponse_ReturnsSuccessWithNullPayload()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIVE.GetEnergyVulnerability", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("{}"));

        var result = await sut.GetVulnerabilityAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetVulnerabilityAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIVE.GetEnergyVulnerability", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetVulnerabilityAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetVulnerabilityAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIVE.GetEnergyVulnerability", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("##"));

        var result = await sut.GetVulnerabilityAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
