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

/// <summary>Unit tests for <see cref="SiaasClient"/> — the SIAAS social-assistance facade.</summary>
public class SiaasClientTests
{
    private const string ValidIdnp = "2000123456782";

    private static (SiaasClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new SiaasClient(mconnect, new TestClock(), NullLogger<SiaasClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetAssistanceAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetAssistanceAsync("0000000000000");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAssistanceAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload =
            "{\"isBeneficiary\":true,\"monthlyAllowanceMdl\":1500.00,\"grantedOn\":\"2024-06-15\",\"programCode\":\"AP-001\"}";
        mconnect.CallAsync("SIAAS.GetSocialAssistance", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetAssistanceAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.IsBeneficiary.Should().BeTrue();
        result.Value.MonthlyAllowanceMdl.Should().Be(1500.00m);
        result.Value.ProgramCode.Should().Be("AP-001");
    }

    [Fact]
    public async Task GetAssistanceAsync_NotFoundResponse_ReturnsSuccessWithNullPayload()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIAAS.GetSocialAssistance", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("null"));

        var result = await sut.GetAssistanceAsync(ValidIdnp);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetAssistanceAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIAAS.GetSocialAssistance", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetAssistanceAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetAssistanceAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SIAAS.GetSocialAssistance", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("not-json"));

        var result = await sut.GetAssistanceAsync(ValidIdnp);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
