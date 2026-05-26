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
/// Unit tests for <see cref="RspClient"/>. Verifies that the facade routes through
/// <see cref="IMConnectClient"/> with the documented serviceCode, validates the IDNP
/// before dispatch, deserialises camelCase JSON, and maps upstream and JSON failures to
/// the contract-stable <see cref="ErrorCodes.MConnectFailed"/>.
/// </summary>
public class RspClientTests
{
    private const string ValidIdnp = "2000123456782";

    private static (RspClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var clock = new TestClock();
        var sut = new RspClient(mconnect, clock, NullLogger<RspClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetByIdnpAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetByIdnpAsync("not-an-idnp");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdnpAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload =
            "{\"idnp\":\"2000123456782\",\"lastName\":\"Popescu\",\"firstName\":\"Ion\",\"patronymic\":\"V.\"," +
            "\"birthDate\":\"1985-04-12\",\"isDeceased\":false,\"dateOfDeath\":null,\"address\":\"str. X 1\",\"citizenship\":\"MDA\"}";
        mconnect.CallAsync("RSP.GetPerson", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetByIdnpAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Idnp.Should().Be(ValidIdnp);
        result.Value.LastName.Should().Be("Popescu");
        result.Value.FirstName.Should().Be("Ion");
        result.Value.Patronymic.Should().Be("V.");
        result.Value.IsDeceased.Should().BeFalse();
        result.Value.Citizenship.Should().Be("MDA");

        await mconnect.Received(1).CallAsync(
            "RSP.GetPerson",
            Arg.Is<string>(s => s.Contains("\"idnp\":\"2000123456782\"") && s.Contains("asOfUtc")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdnpAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("RSP.GetPerson", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetByIdnpAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetByIdnpAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("RSP.GetPerson", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("not-json-at-all"));

        var result = await sut.GetByIdnpAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
