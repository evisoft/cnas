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
/// Unit tests for <see cref="EessiClient"/>. EESSI lookup keys are foreign references
/// (member-state ISO 3166 alpha-2 + foreign SSN), not Moldovan IDNP/IDNO, so input
/// validation focuses on the ISO 3166 shape and non-blank SSN rather than national
/// checksums.
/// </summary>
public class EessiClientTests
{
    private const string MemberState = "RO";
    private const string ForeignSsn = "1234567890123";

    private static (EessiClient sut, IMConnectClient mconnect) Build()
    {
        var mconnect = Substitute.For<IMConnectClient>();
        var sut = new EessiClient(mconnect, new TestClock(), NullLogger<EessiClient>.Instance);
        return (sut, mconnect);
    }

    [Fact]
    public async Task GetByForeignReferenceAsync_InvalidMemberState_ReturnsValidationFailed()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetByForeignReferenceAsync("ROU", ForeignSsn);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByForeignReferenceAsync_BlankSsn_ReturnsValidationFailed()
    {
        var (sut, mconnect) = Build();

        var result = await sut.GetByForeignReferenceAsync(MemberState, "");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        await mconnect.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByForeignReferenceAsync_HappyPath_DelegatesToMConnectAndDeserializes()
    {
        var (sut, mconnect) = Build();
        const string payload = "{\"memberStateCode\":\"RO\",\"foreignSsn\":\"1234567890123\"," +
            "\"lifetimeContributionMdlEquivalent\":98765.43," +
            "\"periods\":[{\"start\":\"2010-01-01\",\"end\":\"2015-12-31\",\"countryCode\":\"RO\",\"status\":\"EMPLOYED\"}]}";
        mconnect.CallAsync("EESSI.GetSocialSecurityRecord", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(payload));

        var result = await sut.GetByForeignReferenceAsync(MemberState, ForeignSsn);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.MemberStateCode.Should().Be("RO");
        result.Value.Periods.Should().ContainSingle();
        result.Value.LifetimeContributionMdlEquivalent.Should().Be(98765.43m);

        await mconnect.Received(1).CallAsync(
            "EESSI.GetSocialSecurityRecord",
            Arg.Is<string>(s => s.Contains("\"memberStateCode\":\"RO\"") && s.Contains("\"foreignSsn\":\"1234567890123\"")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetByForeignReferenceAsync_NotFoundResponse_ReturnsSuccessWithNullPayload()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("EESSI.GetSocialSecurityRecord", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("null"));

        var result = await sut.GetByForeignReferenceAsync(MemberState, ForeignSsn);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task GetByForeignReferenceAsync_McConnectFailure_PropagatesFailure()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("EESSI.GetSocialSecurityRecord", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(ErrorCodes.MConnectFailed, "boom"));

        var result = await sut.GetByForeignReferenceAsync(MemberState, ForeignSsn);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }

    [Fact]
    public async Task GetByForeignReferenceAsync_GarbageJsonResponse_ReturnsMConnectFailed()
    {
        var (sut, mconnect) = Build();
        mconnect.CallAsync("EESSI.GetSocialSecurityRecord", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("nope"));

        var result = await sut.GetByForeignReferenceAsync(MemberState, ForeignSsn);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.MConnectFailed);
    }
}
