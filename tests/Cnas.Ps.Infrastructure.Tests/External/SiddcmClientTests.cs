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

/// <summary>
/// Unit tests for <see cref="SiddcmClient"/>. The disability registry returns either a
/// JSON record, the literal <c>null</c>, or an empty object — all need to map cleanly
/// to <c>Success(null)</c> in the facade.
/// </summary>
public class SiddcmClientTests
{
    private const string ValidIdnp = "2000123456782";

    private static (SiddcmClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new SiddcmClient(mconnect, new TestClock(), NullLogger<SiddcmClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetDisabilityAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetDisabilityAsync("zzz");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDisabilityAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload =
            "{\"degree\":\"SEVERE\",\"evaluatedAtUtc\":\"2024-02-10T09:30:00Z\"," +
            "\"validUntilUtc\":\"2027-02-10T09:30:00Z\",\"commissionRef\":\"CNDDCM-2024-1\"}";
        mconnect.CallAsync("SIDDCM.GetDisabilityStatus", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetDisabilityAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Degree.Should().Be("SEVERE");
        result.Value.CommissionRef.Should().Be("CNDDCM-2024-1");

        await mconnect.Received(1).CallAsync(
            "SIDDCM.GetDisabilityStatus",
            Arg.Is<string>(s => s.Contains("\"idnp\":\"2000123456782\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDisabilityAsync_NotFoundResponse_ReturnsSuccessWithNullPayload()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIDDCM.GetDisabilityStatus", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("null"));

        var result = await sut.GetDisabilityAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetDisabilityAsync_EmptyObjectResponse_ReturnsSuccessWithNullPayload()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIDDCM.GetDisabilityStatus", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("{}"));

        var result = await sut.GetDisabilityAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetDisabilityAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIDDCM.GetDisabilityStatus", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetDisabilityAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetDisabilityAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIDDCM.GetDisabilityStatus", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("garbage"));

        var result = await sut.GetDisabilityAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
