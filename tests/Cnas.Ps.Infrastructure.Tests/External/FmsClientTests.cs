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
/// Unit tests for <see cref="FmsClient"/>. FMS is keyed by CNAS-internal treasury
/// reference rather than IDNP, so there is no IDNP validation step — instead the client
/// rejects blank references with <see cref="ErrorCodes.ValidationFailed"/>.
/// </summary>
public class FmsClientTests
{
    private const string Ref = "CNAS-ACC-001";

    private static (FmsClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new FmsClient(mconnect, new TestClock(), NullLogger<FmsClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetAccountStateAsync_BlankReference_ReturnsValidationFailed()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetAccountStateAsync("   ");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccountStateAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload = "{\"currentBalanceMdl\":1234.56,\"asOfUtc\":\"2026-05-19T08:00:00Z\"," +
            "\"recentTransactions\":[{\"postedAtUtc\":\"2026-05-18T12:00:00Z\",\"amountMdl\":-100,\"referenceNumber\":\"T1\",\"description\":\"pay\"}]}";
        mconnect.CallAsync("FMS.GetCnasAccountState", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetAccountStateAsync(Ref);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentBalanceMdl.Should().Be(1234.56m);
        result.Value.RecentTransactions.Should().ContainSingle();
        result.Value.RecentTransactions[0].ReferenceNumber.Should().Be("T1");

        await mconnect.Received(1).CallAsync(
            "FMS.GetCnasAccountState",
            Arg.Is<string>(s => s.Contains("CNAS-ACC-001")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccountStateAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("FMS.GetCnasAccountState", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetAccountStateAsync(Ref);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetAccountStateAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("FMS.GetCnasAccountState", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("###"));

        var result = await sut.GetAccountStateAsync(Ref);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
