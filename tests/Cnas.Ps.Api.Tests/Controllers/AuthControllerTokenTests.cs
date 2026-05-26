using Cnas.Ps.Api.Controllers;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.Identity;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="AuthController"/>'s R0053 token endpoints
/// (<c>POST /api/auth/token</c> + <c>POST /api/auth/logout</c>). Direct construction with
/// NSubstitute fakes of <see cref="IRefreshTokenService"/> and
/// <see cref="IJwtTokenIssuer"/>; mirrors the pattern in
/// <see cref="ApplicationsControllerTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints accept the deferred login flow (<c>grantType=password</c>) only
/// as a 501-Not-Implemented hand-off to R0051 — the actual local-login wiring
/// is out of R0053's scope. The remaining branches cover the refresh-token grant
/// + logout, including the reuse-detection path that revokes the entire family.
/// </para>
/// </remarks>
public sealed class AuthControllerTokenTests
{
    /// <summary>Builds a fresh refresh-token service substitute.</summary>
    private static IRefreshTokenService NewRefreshSvcMock() =>
        Substitute.For<IRefreshTokenService>();

    /// <summary>Builds a fresh JWT issuer substitute.</summary>
    private static IJwtTokenIssuer NewJwtIssuerMock() =>
        Substitute.For<IJwtTokenIssuer>();

    /// <summary>Builds a fresh local-login service substitute (R0051 wiring).</summary>
    private static ILocalLoginService NewLocalLoginMock() =>
        Substitute.For<ILocalLoginService>();

    /// <summary>Builds the SUT around the supplied substitutes.</summary>
    private static AuthController NewController(
        IRefreshTokenService? refreshSvc = null,
        IJwtTokenIssuer? jwtIssuer = null,
        ILocalLoginService? localLoginSvc = null)
        => new(
            refreshSvc ?? NewRefreshSvcMock(),
            jwtIssuer ?? NewJwtIssuerMock(),
            localLoginSvc ?? NewLocalLoginMock());

    private static readonly DateTime IssuedAt = new(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Stable canonical roles list reused across the password-grant happy-path tests.</summary>
    private static readonly string[] EffectiveRolesFixture = ["utilizator-autorizat"];

    // ─────────────────────── POST /api/auth/token ───────────────────────

    [Fact]
    public async Task Token_MissingRefreshToken_Returns400()
    {
        // GrantType says refresh_token but the body omits the token itself.
        var controller = NewController();
        var body = new IssueTokenRequest("refresh_token", RefreshToken: null);

        var result = await controller.TokenAsync(body, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Token_UnknownRefreshToken_Returns401()
    {
        var refresh = NewRefreshSvcMock();
        refresh.RotateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<RefreshTokenIssueResult>.Failure(
                ErrorCodes.RefreshTokenInvalid, "no such token."));
        var controller = NewController(refresh);
        var body = new IssueTokenRequest("refresh_token", "totally-bogus");

        var result = await controller.TokenAsync(body, CancellationToken.None);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Token_ValidRefreshToken_Returns200_WithAccessAndNewRefresh()
    {
        const string oldRefresh = "old-refresh-token";
        const string newRefresh = "fresh-refresh-token";
        var familyId = Guid.NewGuid();
        var refreshExpires = IssuedAt.AddDays(30);
        var accessExpires = IssuedAt.AddMinutes(15);

        var refresh = NewRefreshSvcMock();
        refresh.RotateAsync(oldRefresh, Arg.Any<CancellationToken>())
            .Returns(Result<RefreshTokenIssueResult>.Success(
                new RefreshTokenIssueResult(newRefresh, familyId, refreshExpires, UserId: 42L)));

        var jwt = NewJwtIssuerMock();
        jwt.IssueAccessToken(Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<string>>())
            .Returns(("jwt.access.token", accessExpires));

        var controller = NewController(refresh, jwt);
        var body = new IssueTokenRequest("refresh_token", oldRefresh);

        var result = await controller.TokenAsync(body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var token = ok.Value.Should().BeOfType<TokenResponse>().Subject;
        token.AccessToken.Should().Be("jwt.access.token");
        token.RefreshToken.Should().Be(newRefresh);
        token.RefreshToken.Should().NotBe(oldRefresh, "rotation MUST issue a new refresh token.");
        token.AccessTokenExpiresAtUtc.Should().Be(accessExpires);
        token.RefreshTokenExpiresAtUtc.Should().Be(refreshExpires);
    }

    [Fact]
    public async Task Token_ReuseDetected_Returns401()
    {
        // The refresh service returned the reuse-detected code — the controller maps to 401.
        // The actual family revoke happened inside the service; the controller does not need
        // to assert against the DB here (the service-level test covers persistence).
        var refresh = NewRefreshSvcMock();
        refresh.RotateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<RefreshTokenIssueResult>.Failure(
                ErrorCodes.RefreshTokenReused,
                "Refresh token has already been consumed — family revoked."));
        var controller = NewController(refresh);
        var body = new IssueTokenRequest("refresh_token", "stolen-token");

        var result = await controller.TokenAsync(body, CancellationToken.None);

        var status = result.Result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Token_PasswordGrant_MissingFields_Returns400_WithLoginInvalid()
    {
        // R0051 — empty Login / Password collapses to 400 with the stable
        // LOGIN.INVALID code (account-enumeration prevention: same code as bad
        // password / wrong role / unknown login).
        var controller = NewController();
        var body = new IssueTokenRequest("password", null, Login: null, Password: null);

        var result = await controller.TokenAsync(body, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var pd = problem.Value.Should().BeOfType<Microsoft.AspNetCore.Mvc.ProblemDetails>().Subject;
        pd.Title.Should().Be(ErrorCodes.LoginInvalid);
    }

    [Fact]
    public async Task Token_PasswordGrant_BadCredentials_Returns400()
    {
        // The local-login service refused — the controller maps every LOGIN.INVALID
        // result to HTTP 400 with that stable error code on the body.
        var local = Substitute.For<ILocalLoginService>();
        local.LoginAsync(Arg.Any<LocalLoginInputDto>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<LocalLoginSuccessDto>.Failure(
                ErrorCodes.LoginInvalid, "Invalid credentials."));
        var controller = NewController(localLoginSvc: local);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        var body = new IssueTokenRequest("password", null, "alice", "wrong-password");

        var result = await controller.TokenAsync(body, CancellationToken.None);

        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Token_PasswordGrant_Success_Returns200_WithEnvelope()
    {
        // R0051 happy path — the service returned a populated envelope; the
        // controller surfaces it as an Ok(LocalLoginSuccessDto).
        var issuedAt = IssuedAt;
        var envelope = new LocalLoginSuccessDto(
            AccessToken: "jwt.access.token",
            AccessTokenExpiresAtUtc: issuedAt.AddMinutes(15),
            RefreshToken: "opaque-refresh",
            RefreshTokenExpiresAtUtc: issuedAt.AddDays(30),
            UserSqid: "SQID-7",
            DisplayName: "Alice Tester",
            EffectiveRoles: EffectiveRolesFixture);

        var local = Substitute.For<ILocalLoginService>();
        local.LoginAsync(Arg.Any<LocalLoginInputDto>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<LocalLoginSuccessDto>.Success(envelope));
        var controller = NewController(localLoginSvc: local);
        // The controller reads HttpContext.Connection.RemoteIpAddress; supply a
        // default context so the property access does not NRE.
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        var body = new IssueTokenRequest("password", null, "alice", "Aa1!aaaa");

        var result = await controller.TokenAsync(body, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<LocalLoginSuccessDto>().Subject;
        dto.AccessToken.Should().Be("jwt.access.token");
        dto.UserSqid.Should().Be("SQID-7");
        dto.EffectiveRoles.Should().Contain("utilizator-autorizat");
    }

    // ─────────────────────── POST /api/auth/logout ───────────────────────

    [Fact]
    public async Task Logout_KnownRefreshToken_Returns204()
    {
        var refresh = NewRefreshSvcMock();
        refresh.RevokeFamilyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var controller = NewController(refresh);

        var result = await controller.LogoutAsync(
            new LogoutRequest("known-token"), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        await refresh.Received(1).RevokeFamilyAsync(
            "known-token", "logout", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Logout_UnknownRefreshToken_Returns204()
    {
        // Logout is idempotent — the service returns success even for unknown tokens.
        // The controller surfaces 204 in both cases.
        var refresh = NewRefreshSvcMock();
        refresh.RevokeFamilyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var controller = NewController(refresh);

        var result = await controller.LogoutAsync(
            new LogoutRequest("never-issued"), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Logout_MissingRefreshToken_Returns400()
    {
        // No token supplied — the controller short-circuits to 400 before calling into
        // the service.
        var refresh = NewRefreshSvcMock();
        var controller = NewController(refresh);

        var result = await controller.LogoutAsync(
            new LogoutRequest(string.Empty), CancellationToken.None);

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await refresh.DidNotReceiveWithAnyArgs().RevokeFamilyAsync(
            default!, default!, default);
    }
}
