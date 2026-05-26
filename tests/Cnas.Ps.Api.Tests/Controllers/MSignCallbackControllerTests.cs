using System.Threading.Tasks;
using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Direct-construction unit tests for <see cref="MSignCallbackController"/>. MSign POSTs
/// to this endpoint server-to-server when a signing request becomes ready, so the
/// controller is exposed anonymously — the trust boundary is the mTLS handshake at the
/// gateway, not an Authorization header.
/// </summary>
public sealed class MSignCallbackControllerTests
{
    private static ICallbackSignatureVerifier AllowingVerifier()
    {
        var verifier = Substitute.For<ICallbackSignatureVerifier>();
        verifier.Verify(
                CallbackSignatureProvider.MSign,
                Arg.Any<string>(),
                Arg.Any<IHeaderDictionary>())
            .Returns(CallbackSignatureVerificationResult.Success());
        return verifier;
    }

    private static ICallbackSignatureVerifier RejectingVerifier()
    {
        var verifier = Substitute.For<ICallbackSignatureVerifier>();
        verifier.Verify(
                CallbackSignatureProvider.MSign,
                Arg.Any<string>(),
                Arg.Any<IHeaderDictionary>())
            .Returns(CallbackSignatureVerificationResult.Failure("signature missing"));
        return verifier;
    }

    private static MSignCallbackController BuildSut(ICallbackSignatureVerifier? verifier = null)
    {
        var sut = new MSignCallbackController(
            NullLogger<MSignCallbackController>.Instance,
            verifier ?? AllowingVerifier());
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return sut;
    }

    [Fact]
    public async Task Callback_ValidRequestId_Returns200()
    {
        var sut = BuildSut();

        var result = await sut.CallbackAsync("RID-42", default);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Callback_EmptyRequestId_Returns400()
    {
        var sut = BuildSut();

        var result = await sut.CallbackAsync(string.Empty, default);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Callback_MissingValidSignature_Returns401()
    {
        var sut = BuildSut(RejectingVerifier());

        var result = await sut.CallbackAsync("RID-UNSIGNED", default);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }
}
