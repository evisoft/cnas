using System.Net;
using System.Text.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Web.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Backend;

/// <summary>
/// Tests for the lightweight authentication-probe service that sits in front of
/// <c>GET /api/profile/me</c> and caches the response for the duration of a single
/// page navigation.
/// </summary>
public sealed class CookieAuthStateTests
{
    private static (CnasApiClient api, MockHttpMessageHandler mock) BuildApi()
    {
        var mock = new MockHttpMessageHandler();
        var http = mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        return (new CnasApiClient(http, NullLogger<CnasApiClient>.Instance), mock);
    }

    [Fact]
    public async Task GetSession_WhenProfileEndpointSucceeds_ReturnsAuthenticatedSession()
    {
        var (api, mock) = BuildApi();
        var profile = new ProfileOutput("u1", "Ion Popescu", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>());
        mock.When("https://api.test/api/profile/me")
            .Respond("application/json", JsonSerializer.Serialize(profile));

        var auth = new CookieAuthState(api);

        var session = await auth.GetSessionAsync().ConfigureAwait(true);

        session.IsAuthenticated.Should().BeTrue();
        session.DisplayName.Should().Be("Ion Popescu");
    }

    [Fact]
    public async Task GetSession_WhenProfileEndpointReturns401_ReturnsAnonymous()
    {
        var (api, mock) = BuildApi();
        mock.When("https://api.test/api/profile/me")
            .Respond(HttpStatusCode.Unauthorized);

        var auth = new CookieAuthState(api);

        var session = await auth.GetSessionAsync().ConfigureAwait(true);

        session.IsAuthenticated.Should().BeFalse();
        session.DisplayName.Should().Be(string.Empty);
    }

    [Fact]
    public async Task Invalidate_ClearsCache_ForcingNextCallToReprobe()
    {
        var (api, mock) = BuildApi();
        var profile = new ProfileOutput("u1", "First", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>());
        var profile2 = new ProfileOutput("u1", "Second", null, null, "ro", Array.Empty<IssuedDocumentSummaryDto>());
        var calls = 0;
        mock.When("https://api.test/api/profile/me")
            .Respond(_ =>
            {
                calls++;
                var body = calls == 1 ? profile : profile2;
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
                };
                return resp;
            });

        var auth = new CookieAuthState(api);

        var first = await auth.GetSessionAsync().ConfigureAwait(true);
        auth.Invalidate();
        var second = await auth.GetSessionAsync().ConfigureAwait(true);

        first.DisplayName.Should().Be("First");
        second.DisplayName.Should().Be("Second");
        calls.Should().Be(2);
    }
}
