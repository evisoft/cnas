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

/// <summary>Unit tests for <see cref="SiaIssClient"/> — the SIAÎSȘ facade.</summary>
public class SiaIssClientTests
{
    private const string ValidIdnp = "2000123456782";

    private static (SiaIssClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new SiaIssClient(mconnect, new TestClock(), NullLogger<SiaIssClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetUnemploymentAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetUnemploymentAsync("nope");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUnemploymentAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload =
            "{\"isRegistered\":true,\"registeredOn\":\"2024-09-01\",\"unregisteredOn\":null,\"receivesAllowance\":true}";
        mconnect.CallAsync("SIAISS.GetUnemploymentStatus", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetUnemploymentAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.IsRegistered.Should().BeTrue();
        result.Value.ReceivesAllowance.Should().BeTrue();

        await mconnect.Received(1).CallAsync(
            "SIAISS.GetUnemploymentStatus",
            Arg.Is<string>(s => s.Contains("\"idnp\":\"2000123456782\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUnemploymentAsync_NotFoundResponse_ReturnsSuccessWithNullPayload()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIAISS.GetUnemploymentStatus", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("null"));

        var result = await sut.GetUnemploymentAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetUnemploymentAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIAISS.GetUnemploymentStatus", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetUnemploymentAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetUnemploymentAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIAISS.GetUnemploymentStatus", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("not-json"));

        var result = await sut.GetUnemploymentAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
