using Cnas.Ps.Api.Composition;
using Cnas.Ps.Infrastructure.MGov;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Tests.Composition;

public sealed class AuthenticationCompositionTests
{
    [Fact]
    public void AddCnasAuthentication_ProductionCookieRequiresSecureTransport()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddCnasAuthentication(configuration);

        using var provider = services.BuildServiceProvider();
        var cookie = provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        cookie.Cookie.SecurePolicy.Should().Be(CookieSecurePolicy.Always);
    }

    [Fact]
    public void AddCnasAuthentication_DoesNotPersistOidcTokensInSessionCookie()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                [$"{MGovOptions.SectionName}:MPassIssuer"] = "https://mpass.example.test",
                [$"{MGovOptions.SectionName}:MPassClientId"] = "cnas",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddCnasAuthentication(configuration);

        using var provider = services.BuildServiceProvider();
        var oidc = provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        oidc.SaveTokens.Should().BeFalse();
    }
}
