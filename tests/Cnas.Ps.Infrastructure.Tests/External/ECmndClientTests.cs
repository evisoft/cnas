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
/// Unit tests for <see cref="ECmndClient"/>. Covers the closed-set <c>actKind</c>
/// validation rule in addition to the standard four MConnect-facade scenarios.
/// </summary>
public class ECmndClientTests
{
    private const string ValidIdnp = "2000123456782";

    private static (ECmndClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new ECmndClient(mconnect, new TestClock(), NullLogger<ECmndClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetCivilActAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetCivilActAsync("x", "BIRTH");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCivilActAsync_UnknownActKind_ReturnsValidationFailed()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetCivilActAsync(ValidIdnp, "ADOPTION");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCivilActAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload = "{\"actNumber\":\"A-2024-100\",\"actKind\":\"BIRTH\",\"actDate\":\"1985-04-12\"," +
            "\"issuerOffice\":\"OSC-CHISINAU\",\"attributes\":{\"fatherName\":\"V. Popescu\",\"motherName\":\"M. Popescu\"}}";
        mconnect.CallAsync("ECMND.GetCivilAct", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetCivilActAsync(ValidIdnp, "BIRTH");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.ActNumber.Should().Be("A-2024-100");
        result.Value.ActKind.Should().Be("BIRTH");
        result.Value.Attributes.Should().ContainKey("fatherName");

        await mconnect.Received(1).CallAsync(
            "ECMND.GetCivilAct",
            Arg.Is<string>(s => s.Contains("\"actKind\":\"BIRTH\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCivilActAsync_NotFoundResponse_ReturnsSuccessWithNullPayload()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("ECMND.GetCivilAct", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("null"));

        var result = await sut.GetCivilActAsync(ValidIdnp, "DEATH");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetCivilActAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("ECMND.GetCivilAct", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetCivilActAsync(ValidIdnp, "MARRIAGE");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetCivilActAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("ECMND.GetCivilAct", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("###"));

        var result = await sut.GetCivilActAsync(ValidIdnp, "DIVORCE");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
