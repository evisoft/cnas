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
/// Unit tests for <see cref="SfsClient"/>. Confirms that the SFS facade validates the
/// IDNP, accepts both envelope and bare-array response shapes, and surfaces upstream
/// failures verbatim.
/// </summary>
public class SfsClientTests
{
    private const string ValidIdnp = "2000123456782";

    private static (SfsClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new SfsClient(mconnect, new TestClock(), NullLogger<SfsClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetDeclarationsAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetDeclarationsAsync("xxx", 2025);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDeclarationsAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload = "{\"declarations\":[" +
            "{\"year\":2025,\"month\":1,\"grossSalaryMdl\":12000.00,\"contributionMdl\":2880.00,\"employerIdno\":\"1003600012346\"}," +
            "{\"year\":2025,\"month\":2,\"grossSalaryMdl\":12500.00,\"contributionMdl\":3000.00,\"employerIdno\":\"1003600012346\"}]}";
        mconnect.CallAsync("SFS.GetSalaryDeclarations", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetDeclarationsAsync(ValidIdnp, 2025);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value[0].Month.Should().Be(1);
        result.Value[0].GrossSalaryMdl.Should().Be(12000.00m);

        await mconnect.Received(1).CallAsync(
            "SFS.GetSalaryDeclarations",
            Arg.Is<string>(s => s.Contains("\"idnp\":\"2000123456782\"") && s.Contains("\"year\":2025")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDeclarationsAsync_BareArrayResponse_AlsoAccepted()
    {
        var (sut, mconnect) = Build();
        const string payload = "[{\"year\":2025,\"month\":3,\"grossSalaryMdl\":11000,\"contributionMdl\":2640,\"employerIdno\":\"1003600012346\"}]";
        mconnect.CallAsync("SFS.GetSalaryDeclarations", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetDeclarationsAsync(ValidIdnp, 2025);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].Month.Should().Be(3);
    }

    [Fact]
    public async Task GetDeclarationsAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SFS.GetSalaryDeclarations", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetDeclarationsAsync(ValidIdnp, 2025);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetDeclarationsAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("SFS.GetSalaryDeclarations", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("\"not-a-list\""));

        var result = await sut.GetDeclarationsAsync(ValidIdnp, 2025);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
