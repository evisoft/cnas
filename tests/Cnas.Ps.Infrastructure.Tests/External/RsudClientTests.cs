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
/// Unit tests for <see cref="RsudClient"/> — the typed facade over MConnect that retrieves
/// legal-person records from RSUD. Mirrors <see cref="RspClientTests"/> in structure.
/// </summary>
public class RsudClientTests
{
    // A known-valid Moldovan IDNO (mod-10 checksum verified). Used in every happy-path test.
    private const string ValidIdno = "1003600012346";

    private static (RsudClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new RsudClient(mconnect, new TestClock(), NullLogger<RsudClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetByIdnoAsync_InvalidIdno_ReturnsInvalidIdno()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetByIdnoAsync("0000000000000");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdno);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdnoAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload =
            "{\"idno\":\"1003600012346\",\"name\":\"ACME SRL\",\"legalForm\":\"SRL\",\"registeredOn\":\"2010-03-01\",\"isActive\":true,\"address\":\"mun. Chișinău\"}";
        mconnect.CallAsync("RSUD.GetLegalPerson", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetByIdnoAsync(ValidIdno);

        result.IsSuccess.Should().BeTrue();
        result.Value.Idno.Should().Be(ValidIdno);
        result.Value.Name.Should().Be("ACME SRL");
        result.Value.LegalForm.Should().Be("SRL");
        result.Value.IsActive.Should().BeTrue();

        await mconnect.Received(1).CallAsync(
            "RSUD.GetLegalPerson",
            Arg.Is<string>(s => s.Contains("\"idno\":\"1003600012346\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByIdnoAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("RSUD.GetLegalPerson", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetByIdnoAsync(ValidIdno);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetByIdnoAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("RSUD.GetLegalPerson", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("<<<not-json>>>"));

        var result = await sut.GetByIdnoAsync(ValidIdno);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
