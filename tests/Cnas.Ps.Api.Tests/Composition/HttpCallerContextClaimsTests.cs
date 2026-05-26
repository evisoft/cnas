using System.Security.Claims;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Composition;

/// <summary>
/// Tests covering the MPower-via-MPass claim reading on <see cref="HttpCallerContext"/>.
/// Verifies that the <c>mpower:principal_idnp</c> and <c>mpower:delegation_id</c> claims
/// flow from the authenticated <see cref="ClaimsPrincipal"/> into
/// <see cref="ICallerContext.OnBehalfOfPrincipalIdnp"/> and
/// <see cref="ICallerContext.DelegationPowerId"/> as documented on
/// <c>docs/EGOV-INTEGRATION-GAP.md</c> §"MPower".
/// </summary>
public sealed class HttpCallerContextClaimsTests
{
    /// <summary>Builds an <see cref="HttpCallerContext"/> backed by a principal carrying the supplied claims.</summary>
    private static HttpCallerContext Build(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        var sqids = Substitute.For<ISqidService>();
        return new HttpCallerContext(accessor, sqids);
    }

    [Fact]
    public void OnBehalfOfPrincipalIdnp_ClaimPresent_ReturnsValue()
    {
        var sut = Build(new Claim("mpower:principal_idnp", "2000000000015"));

        sut.OnBehalfOfPrincipalIdnp.Should().Be("2000000000015");
    }

    [Fact]
    public void OnBehalfOfPrincipalIdnp_ClaimMissing_ReturnsNull()
    {
        var sut = Build();

        sut.OnBehalfOfPrincipalIdnp.Should().BeNull();
    }

    [Fact]
    public void DelegationPowerId_ClaimPresent_ReturnsValue()
    {
        var sut = Build(new Claim("mpower:delegation_id", "DEL-9af3"));

        sut.DelegationPowerId.Should().Be("DEL-9af3");
    }

    [Fact]
    public void DelegationPowerId_ClaimMissing_ReturnsNull()
    {
        var sut = Build();

        sut.DelegationPowerId.Should().BeNull();
    }

    [Fact]
    public void OnBehalfOfPrincipalIdnp_WhitespaceOnly_ReturnsNull()
    {
        // Defensive: whitespace-only claim values are normalised to null so the service
        // layer can treat "absent" and "explicitly blank" uniformly.
        var sut = Build(new Claim("mpower:principal_idnp", "   "));

        sut.OnBehalfOfPrincipalIdnp.Should().BeNull();
    }
}
