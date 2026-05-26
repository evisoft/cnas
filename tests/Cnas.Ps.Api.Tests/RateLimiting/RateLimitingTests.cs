using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Cnas.Ps.Api.Composition;
using Cnas.Ps.Core.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Api.Tests.RateLimiting;

/// <summary>
/// End-to-end tests for the CNAS rate-limiting composition (CLAUDE.md §5.3).
/// Each scenario hits a real Kestrel host with a real
/// <see cref="System.Threading.RateLimiting.RateLimiter"/> behind it — the limiter SDK
/// has no test mode, so the only honest way to verify behaviour is to fire requests
/// and assert on responses.
/// </summary>
public sealed class RateLimitingTests
{
    [Fact]
    public void Options_DefaultsDoNotTrustForwardedHeaders()
    {
        new RateLimitingOptions().TrustForwardedHeaders.Should().BeFalse();
    }

    /// <summary>
    /// Builds a per-test configuration dictionary. Defaults to small permit limits and
    /// short windows so the whole suite finishes quickly without losing fidelity.
    /// </summary>
    private static Dictionary<string, string?> BaseConfig(
        int anonymousLimit = 3,
        int authenticatedLimit = 5,
        int uploadLimit = 2,
        int callbackLimit = 10,
        int windowSeconds = 5,
        bool enabled = true,
        bool trustForwardedHeaders = true)
        => new()
        {
            ["Cnas:RateLimiting:Enabled"] = enabled ? "true" : "false",
            ["Cnas:RateLimiting:TrustForwardedHeaders"] = trustForwardedHeaders ? "true" : "false",
            ["Cnas:RateLimiting:Anonymous:PermitLimit"] = anonymousLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cnas:RateLimiting:Anonymous:WindowSeconds"] = windowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cnas:RateLimiting:Anonymous:QueueLimit"] = "0",
            ["Cnas:RateLimiting:Authenticated:PermitLimit"] = authenticatedLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cnas:RateLimiting:Authenticated:WindowSeconds"] = windowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cnas:RateLimiting:Authenticated:QueueLimit"] = "0",
            ["Cnas:RateLimiting:Upload:PermitLimit"] = uploadLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cnas:RateLimiting:Upload:WindowSeconds"] = windowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cnas:RateLimiting:Upload:QueueLimit"] = "0",
            ["Cnas:RateLimiting:Callback:PermitLimit"] = callbackLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cnas:RateLimiting:Callback:WindowSeconds"] = windowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cnas:RateLimiting:Callback:QueueLimit"] = "0",
        };

    private static HttpRequestMessage Get(string path, string? forwardedFor = null, string? testUser = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            req.Headers.Add("X-Forwarded-For", forwardedFor);
        }
        if (!string.IsNullOrEmpty(testUser))
        {
            req.Headers.Add(TestAuthHandler.HeaderName, testUser);
        }
        return req;
    }

    [Fact]
    public async Task Anonymous_OverLimit_Returns429()
    {
        await using var host = await RateLimitingTestHost.StartAsync(
            BaseConfig(anonymousLimit: 5, windowSeconds: 30));
        using var client = host.CreateClient();

        // Fire 5 anonymous requests — all should succeed (window = 30s so they fit).
        for (var i = 0; i < 5; i++)
        {
            using var ok = await client.SendAsync(Get("/test/anonymous", forwardedFor: "203.0.113.7"));
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"request #{i + 1} should be admitted");
        }

        // 6th request blows the bucket -> 429.
        using var rejected = await client.SendAsync(Get("/test/anonymous", forwardedFor: "203.0.113.7"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull("Retry-After is mandated by RFC 6585");
        var body = await rejected.Content.ReadAsStringAsync();
        body.Should().Contain(RateLimitingPolicies.Anonymous);
    }

    [Fact]
    public async Task Anonymous_DifferentIPs_NotShared()
    {
        await using var host = await RateLimitingTestHost.StartAsync(
            BaseConfig(anonymousLimit: 3, windowSeconds: 30));
        using var client = host.CreateClient();

        // 3 requests from IP A — all good.
        for (var i = 0; i < 3; i++)
        {
            using var r = await client.SendAsync(Get("/test/anonymous", forwardedFor: "198.51.100.10"));
            r.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // 3 requests from IP B — must succeed because the partitions are independent.
        for (var i = 0; i < 3; i++)
        {
            using var r = await client.SendAsync(Get("/test/anonymous", forwardedFor: "198.51.100.20"));
            r.StatusCode.Should().Be(HttpStatusCode.OK, $"IP B request #{i + 1} should not share IP A's bucket");
        }
    }

    [Fact]
    public async Task Authenticated_PartitionedByUser()
    {
        await using var host = await RateLimitingTestHost.StartAsync(
            BaseConfig(authenticatedLimit: 3, windowSeconds: 30));
        using var client = host.CreateClient();

        // User A burns their full quota.
        for (var i = 0; i < 3; i++)
        {
            using var r = await client.SendAsync(Get("/test/authenticated", testUser: "userA"));
            r.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // User A is now throttled.
        using var rejected = await client.SendAsync(Get("/test/authenticated", testUser: "userA"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // User B still has a full quota.
        for (var i = 0; i < 3; i++)
        {
            using var r = await client.SendAsync(Get("/test/authenticated", testUser: "userB"));
            r.StatusCode.Should().Be(HttpStatusCode.OK, $"userB request #{i + 1} should not share userA's bucket");
        }
    }

    [Fact]
    public async Task Upload_LowerLimitThanAuthenticatedDefault()
    {
        // Upload permit limit is intentionally smaller than the Authenticated one.
        await using var host = await RateLimitingTestHost.StartAsync(
            BaseConfig(authenticatedLimit: 10, uploadLimit: 2, windowSeconds: 30));
        using var client = host.CreateClient();

        // Two uploads work.
        for (var i = 0; i < 2; i++)
        {
            using var r = await client.SendAsync(Get("/test/upload", testUser: "uploader"));
            r.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Third upload is rejected even though Authenticated would have allowed it.
        using var rejected = await client.SendAsync(Get("/test/upload", testUser: "uploader"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // And the broader Authenticated bucket for the same user is still intact —
        // proving the Upload policy partitions independently.
        using var authReq = await client.SendAsync(Get("/test/authenticated", testUser: "uploader"));
        authReq.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Callback_HigherLimitThanAnonymous()
    {
        // Callback bucket >> Anonymous bucket; with anonymousLimit=2 and callbackLimit=8,
        // we should be able to do 8 callback hits from a single IP.
        await using var host = await RateLimitingTestHost.StartAsync(
            BaseConfig(anonymousLimit: 2, callbackLimit: 8, windowSeconds: 30));
        using var client = host.CreateClient();

        for (var i = 0; i < 8; i++)
        {
            using var r = await client.SendAsync(Get("/test/callback", forwardedFor: "203.0.113.99"));
            r.StatusCode.Should().Be(HttpStatusCode.OK, $"callback #{i + 1} should be admitted (limit=8)");
        }

        // 9th rejected — confirms the bucket size is exactly 8.
        using var rejected = await client.SendAsync(Get("/test/callback", forwardedFor: "203.0.113.99"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Disabled_AllowsUnlimitedRequests()
    {
        // With Enabled=false the limiter must admit every request, regardless of count.
        await using var host = await RateLimitingTestHost.StartAsync(
            BaseConfig(anonymousLimit: 1, windowSeconds: 30, enabled: false));
        using var client = host.CreateClient();

        // 100 requests against a permitLimit=1 policy — must all succeed because Enabled=false.
        for (var i = 0; i < 100; i++)
        {
            using var r = await client.SendAsync(Get("/test/anonymous", forwardedFor: "203.0.113.7"));
            r.StatusCode.Should().Be(HttpStatusCode.OK, $"request #{i + 1} must pass when limiter is disabled");
        }
    }

    [Fact]
    public async Task Rejected_ResponseShape()
    {
        await using var host = await RateLimitingTestHost.StartAsync(
            BaseConfig(anonymousLimit: 1, windowSeconds: 30));
        using var client = host.CreateClient();

        // Burn the only permit, then capture the rejection.
        using var ok = await client.SendAsync(Get("/test/anonymous", forwardedFor: "192.0.2.7"));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        using var rejected = await client.SendAsync(Get("/test/anonymous", forwardedFor: "192.0.2.7"));
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        rejected.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        // Retry-After must be a positive integer.
        rejected.Headers.RetryAfter.Should().NotBeNull();
        rejected.Headers.RetryAfter!.Delta.Should().NotBeNull();
        rejected.Headers.RetryAfter.Delta!.Value.TotalSeconds.Should().BeGreaterThanOrEqualTo(1);

        var body = await rejected.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be("https://tools.ietf.org/html/rfc6585#section-4");
        root.GetProperty("status").GetInt32().Should().Be(429);
        root.GetProperty("title").GetString().Should().Be(ErrorCodes.RateLimited);
        root.GetProperty("policy").GetString().Should().Be(RateLimitingPolicies.Anonymous);
        root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task HealthCheck_NotRateLimited()
    {
        // Set a comically small anonymous bucket — the /health/ready endpoint must
        // still admit every request because it carries DisableRateLimiting.
        await using var host = await RateLimitingTestHost.StartAsync(
            BaseConfig(anonymousLimit: 1, windowSeconds: 30));
        using var client = host.CreateClient();

        for (var i = 0; i < 50; i++)
        {
            using var r = await client.SendAsync(Get("/health/ready", forwardedFor: "203.0.113.7"));
            r.StatusCode.Should().Be(HttpStatusCode.OK, $"health probe #{i + 1} must always succeed");
        }
    }

    [Fact]
    public async Task AddRateLimiting_InvalidConfig_FailsAtStart()
    {
        // Negative permit limit → DataAnnotations validation trips at host build.
        // ValidateOnStart surfaces the OptionsValidationException synchronously from
        // StartAsync, so we assert the wrapper throws.
        var act = async () =>
        {
            await using var host = await RateLimitingTestHost.StartAsync(new Dictionary<string, string?>
            {
                ["Cnas:RateLimiting:Anonymous:PermitLimit"] = "-1",
                ["Cnas:RateLimiting:Anonymous:WindowSeconds"] = "60",
                ["Cnas:RateLimiting:Anonymous:QueueLimit"] = "0",
            });
        };

        await act.Should().ThrowAsync<OptionsValidationException>(
            "ValidateOnStart must surface negative permit limits as a fail-fast options-validation error");
    }
}
