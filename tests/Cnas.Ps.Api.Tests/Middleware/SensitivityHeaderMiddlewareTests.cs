using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Api.Middleware;
using Cnas.Ps.Application.Sensitivity;
using Cnas.Ps.Contracts.Security;
using Cnas.Ps.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cnas.Ps.Api.Tests.Middleware;

/// <summary>
/// R0228 / TOR SEC 033 — end-to-end tests for <see cref="SensitivityHeaderMiddleware"/>.
/// The middleware decorates every API response with <c>X-CNAS-Sensitivity</c> headers
/// reflecting the highest <see cref="SensitivityLabel"/> on the response payload, and
/// audits one <c>SENSITIVITY.RESTRICTED_ACCESS</c> row per request when any Restricted
/// field is present.
/// </summary>
public sealed class SensitivityHeaderMiddlewareTests
{
    /// <summary>Public DTO surfaced by <c>/public-dto</c>.</summary>
    public sealed record PublicDto(
        [property: SensitivityClassification(SensitivityLabel.Public)] string Id,
        [property: SensitivityClassification(SensitivityLabel.Public)] string CatalogName);

    /// <summary>Restricted DTO surfaced by <c>/restricted-dto</c>.</summary>
    public sealed record RestrictedDto(
        [property: SensitivityClassification(SensitivityLabel.Public)] string Id,
        [property: SensitivityClassification(SensitivityLabel.Confidential)] string DisplayName,
        [property: SensitivityClassification(SensitivityLabel.Restricted)] string Idnp,
        [property: SensitivityClassification(SensitivityLabel.Restricted)] string BankAccount);

    [Fact]
    public async Task PublicDtoResponse_SetsPublicHeader_NoAudit()
    {
        var audit = Substitute.For<ISensitivityAuditService>();
        await using var host = await TestHost.StartAsync(audit);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/public-dto");

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Headers.GetValues("X-CNAS-Sensitivity").Should().ContainSingle()
            .Which.Should().Be("Public");
        response.Headers.Contains("X-CNAS-Sensitivity-Fields").Should().BeFalse(
            "no Restricted fields means the field-list header is omitted entirely.");
        await audit.DidNotReceiveWithAnyArgs().RecordRestrictedAccessAsync(
            default!, default, default!, default);
    }

    [Fact]
    public async Task RestrictedDtoResponse_SetsRestrictedHeader_ListsFields_Audits()
    {
        var audit = Substitute.For<ISensitivityAuditService>();
        await using var host = await TestHost.StartAsync(audit);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/restricted-dto");

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Headers.GetValues("X-CNAS-Sensitivity").Should().ContainSingle()
            .Which.Should().Be("Restricted");

        var fieldsHeader = response.Headers.GetValues("X-CNAS-Sensitivity-Fields").Single();
        fieldsHeader.Split(',').Select(s => s.Trim()).Should()
            .BeEquivalentTo(new[] { nameof(RestrictedDto.Idnp), nameof(RestrictedDto.BankAccount) });

        await audit.Received(1).RecordRestrictedAccessAsync(
            "RestrictedDto",
            Arg.Any<string?>(),
            Arg.Is<IReadOnlyCollection<string>>(fields =>
                fields.Contains(nameof(RestrictedDto.Idnp))
                && fields.Contains(nameof(RestrictedDto.BankAccount))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListOfRestrictedDtoResponse_DetectsGenericArg_AndMarksRestricted()
    {
        var audit = Substitute.For<ISensitivityAuditService>();
        await using var host = await TestHost.StartAsync(audit);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/restricted-list");

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Headers.GetValues("X-CNAS-Sensitivity").Should().ContainSingle()
            .Which.Should().Be("Restricted");
    }

    [Fact]
    public async Task NonDtoResponse_DefaultsToInternal()
    {
        var audit = Substitute.For<ISensitivityAuditService>();
        await using var host = await TestHost.StartAsync(audit);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/file-download");

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Headers.GetValues("X-CNAS-Sensitivity").Should().ContainSingle()
            .Which.Should().Be("Internal",
                "FileResult/redirect responses default to the Internal safety floor.");
        await audit.DidNotReceiveWithAnyArgs().RecordRestrictedAccessAsync(
            default!, default, default!, default);
    }

    [Fact]
    public async Task RestrictedDtoResponse_AuditsExactlyOnce_EvenWithMultipleRestrictedFields()
    {
        var audit = Substitute.For<ISensitivityAuditService>();
        await using var host = await TestHost.StartAsync(audit);
        using var client = host.CreateClient();

        var response = await client.GetAsync("/restricted-dto");

        response.IsSuccessStatusCode.Should().BeTrue();
        await audit.Received(1).RecordRestrictedAccessAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Minimal Kestrel host that wires the sensitivity middleware in front of four
    /// test endpoints (public DTO, restricted DTO, list of restricted DTOs, file
    /// download).
    /// </summary>
    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TestHost(WebApplication app, string baseAddress)
        {
            _app = app;
            BaseAddress = baseAddress;
        }

        public string BaseAddress { get; }

        public HttpClient CreateClient() => new() { BaseAddress = new Uri(BaseAddress) };

        public static async Task<TestHost> StartAsync(ISensitivityAuditService audit)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production",
            });
            builder.Logging.ClearProviders();

            builder.Services.AddSingleton<ISensitivityResolver, SensitivityResolver>();
            builder.Services.AddSingleton(audit);

            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();
            app.UseSensitivityHeaders();

            // TypedResults preserves the DTO type in the endpoint's
            // ProducesResponseTypeMetadata, which is exactly what the middleware reads.
            app.MapGet("/public-dto", () => TypedResults.Ok(new PublicDto("k3Gq9", "Public catalog item")));
            app.MapGet("/restricted-dto", () => TypedResults.Ok(new RestrictedDto(
                Id: "k3Gq9",
                DisplayName: "Ion Popescu",
                Idnp: "2000123456782",
                BankAccount: "MD24AG000225100013104168")));
            app.MapGet("/restricted-list", () => TypedResults.Ok<IReadOnlyList<RestrictedDto>>(new List<RestrictedDto>
            {
                new("k3Gq9", "Ion Popescu", "2000123456782", "MD24AG000225100013104168"),
            }));
            app.MapGet("/file-download", () => Results.File(
                fileContents: new byte[] { 1, 2, 3 },
                contentType: "application/octet-stream",
                fileDownloadName: "blob.bin"));

            await app.StartAsync().ConfigureAwait(false);

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
            var url = addresses.Addresses.First().TrimEnd('/');

            return new TestHost(app, url);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
