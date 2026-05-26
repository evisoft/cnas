using System;
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
/// Unit tests for <see cref="PccmClient"/> — the PCCM (medical certificates) facade.
/// </summary>
public class PccmClientTests
{
    private const string ValidIdnp = "2000123456782";
    private static readonly DateTime From = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    private static (PccmClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new PccmClient(mconnect, new TestClock(), NullLogger<PccmClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetCertificatesAsync_InvalidIdnp_ReturnsInvalidIdnp()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetCertificatesAsync("z", From, To);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidIdnp);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCertificatesAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload = "{\"certificates\":[{" +
            "\"certificateNumber\":\"C-001\",\"issuedOn\":\"2025-03-01\",\"startDate\":\"2025-03-01\",\"endDate\":\"2025-03-07\"," +
            "\"diagnosis\":\"J11.1\",\"issuerCode\":\"IMS-CHISINAU-01\"}]}";
        mconnect.CallAsync("PCCM.GetMedicalCertificates", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetCertificatesAsync(ValidIdnp, From, To);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].CertificateNumber.Should().Be("C-001");
        result.Value[0].Diagnosis.Should().Be("J11.1");

        await mconnect.Received(1).CallAsync(
            "PCCM.GetMedicalCertificates",
            Arg.Is<string>(s => s.Contains("\"idnp\":\"2000123456782\"") && s.Contains("fromUtc") && s.Contains("toUtc")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCertificatesAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("PCCM.GetMedicalCertificates", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetCertificatesAsync(ValidIdnp, From, To);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetCertificatesAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("PCCM.GetMedicalCertificates", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("garbage"));

        var result = await sut.GetCertificatesAsync(ValidIdnp, From, To);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
