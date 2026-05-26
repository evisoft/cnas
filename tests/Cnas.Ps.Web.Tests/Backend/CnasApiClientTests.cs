using System.Net;
using System.Text.Json;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Web.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RichardSzalay.MockHttp;

namespace Cnas.Ps.Web.Tests.Backend;

/// <summary>
/// Unit tests for <see cref="CnasApiClient"/> verifying that
/// (a) successful HTTP responses are deserialised into the expected DTOs and
/// (b) non-2xx responses are mapped to a <see cref="Result{T}"/> failure with
/// the correct <see cref="ErrorCodes"/> constant.
/// </summary>
public sealed class CnasApiClientTests
{
    private static CnasApiClient BuildClient(MockHttpMessageHandler mock)
    {
        var http = mock.ToHttpClient();
        http.BaseAddress = new Uri("https://api.test/");
        return new CnasApiClient(http, NullLogger<CnasApiClient>.Instance);
    }

    [Fact]
    public async Task GetMyApplicationsAsync_HappyPath_DeserializesPagedResult()
    {
        var mock = new MockHttpMessageHandler();
        var paged = new PagedResult<ApplicationListItemOutput>(
            Items: new[]
            {
                new ApplicationListItemOutput("k3Gq9", "Submitted", "REF-001", "u1", DateTime.UtcNow),
            },
            Page: 1, PageSize: 20, TotalCount: 1);
        mock.When("https://api.test/api/applications/mine*")
            .Respond("application/json", JsonSerializer.Serialize(paged));

        var client = BuildClient(mock);

        var r = await client.GetMyApplicationsAsync(1, 20).ConfigureAwait(true);

        r.IsSuccess.Should().BeTrue();
        r.Value.Items.Should().HaveCount(1);
        r.Value.Items[0].Id.Should().Be("k3Gq9");
        r.Value.Items[0].ReferenceNumber.Should().Be("REF-001");
    }

    [Fact]
    public async Task GetApplicationAsync_NotFound_ReturnsFailureWithNotFoundCode()
    {
        var mock = new MockHttpMessageHandler();
        mock.When("https://api.test/api/applications/missing")
            .Respond(HttpStatusCode.NotFound);

        var client = BuildClient(mock);

        var r = await client.GetApplicationAsync("missing").ConfigureAwait(true);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task SubmitApplicationAsync_HappyPath_ReturnsCreatedSqid()
    {
        var mock = new MockHttpMessageHandler();
        var created = new ApplicationOutput("Xm7Yz3", "Submitted", "REF-99", DateTime.UtcNow);
        mock.When("https://api.test/api/applications")
            .Respond(HttpStatusCode.Created, "application/json", JsonSerializer.Serialize(created));

        var client = BuildClient(mock);

        var r = await client.SubmitApplicationAsync(new SubmitApplicationInput("p1", "{}", Array.Empty<string>())).ConfigureAwait(true);

        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be("Xm7Yz3");
    }

    [Fact]
    public async Task SubmitApplicationAsync_BadRequest_ReturnsValidationFailedCode()
    {
        var mock = new MockHttpMessageHandler();
        mock.When("https://api.test/api/applications")
            .Respond(HttpStatusCode.BadRequest, "text/plain", "invalid passport");

        var client = BuildClient(mock);

        var r = await client.SubmitApplicationAsync(new SubmitApplicationInput("p1", "{}", Array.Empty<string>())).ConfigureAwait(true);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        r.ErrorMessage.Should().Contain("invalid passport");
    }

    [Fact]
    public async Task WithdrawApplicationAsync_HappyPath_ReturnsSuccess()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, "https://api.test/api/applications/abc/withdraw")
            .Respond(HttpStatusCode.NoContent);

        var client = BuildClient(mock);

        var r = await client.WithdrawApplicationAsync("abc").ConfigureAwait(true);

        r.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task WithdrawApplicationAsync_Conflict_MapsToConflictErrorCode()
    {
        var mock = new MockHttpMessageHandler();
        mock.When(HttpMethod.Post, "https://api.test/api/applications/abc/withdraw")
            .Respond(HttpStatusCode.Conflict, "text/plain", "already withdrawn");

        var client = BuildClient(mock);

        var r = await client.WithdrawApplicationAsync("abc").ConfigureAwait(true);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task ListServicePassportsAsync_HappyPath_ReturnsList()
    {
        var mock = new MockHttpMessageHandler();
        var passports = new[]
        {
            new ServicePassportListItem("p1", "ALOC", "Alocație de naștere", true, 1),
            new ServicePassportListItem("p2", "PENS", "Pensie", true, 1),
        };
        mock.When("https://api.test/api/service-passports")
            .Respond("application/json", JsonSerializer.Serialize(passports));

        var client = BuildClient(mock);

        var r = await client.ListServicePassportsAsync().ConfigureAwait(true);

        r.IsSuccess.Should().BeTrue();
        r.Value.Should().HaveCount(2);
        r.Value[0].Code.Should().Be("ALOC");
    }

    [Fact]
    public async Task GetServicePassportAsync_HappyPath_ReturnsDetailWithSchema()
    {
        var mock = new MockHttpMessageHandler();
        var detail = new ServicePassportDetailOutput(
            "p1", "ALOC", "Alocație", null, null, "...",
            "{\"required\":[\"a\"],\"properties\":{\"a\":{\"type\":\"string\"}}}",
            "WF", 30, true, false, "{}", 1, true);
        mock.When("https://api.test/api/service-passports/p1")
            .Respond("application/json", JsonSerializer.Serialize(detail));

        var client = BuildClient(mock);

        var r = await client.GetServicePassportAsync("p1").ConfigureAwait(true);

        r.IsSuccess.Should().BeTrue();
        r.Value.Id.Should().Be("p1");
        r.Value.FormSchemaJson.Should().Contain("properties");
    }

    [Fact]
    public async Task GetMyProfileAsync_Unauthorized_MapsToUnauthorizedCode()
    {
        var mock = new MockHttpMessageHandler();
        mock.When("https://api.test/api/profile/me")
            .Respond(HttpStatusCode.Unauthorized);

        var client = BuildClient(mock);

        var r = await client.GetMyProfileAsync().ConfigureAwait(true);

        r.IsFailure.Should().BeTrue();
        r.ErrorCode.Should().Be(ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task GetMyProfileAsync_HappyPath_ReturnsProfile()
    {
        var mock = new MockHttpMessageHandler();
        var profile = new ProfileOutput("u1", "Ion Popescu", "ion@example.md", null, "ro", Array.Empty<IssuedDocumentSummaryDto>());
        mock.When("https://api.test/api/profile/me")
            .Respond("application/json", JsonSerializer.Serialize(profile));

        var client = BuildClient(mock);

        var r = await client.GetMyProfileAsync().ConfigureAwait(true);

        r.IsSuccess.Should().BeTrue();
        r.Value.DisplayName.Should().Be("Ion Popescu");
        r.Value.PreferredLanguage.Should().Be("ro");
    }
}
