using Cnas.Ps.Application.ExternalSources;
using Cnas.Ps.Infrastructure.Services.ExternalSources;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.ExternalSources;

/// <summary>
/// R0203 / TOR CF 20.06 — tests for <see cref="RspExternalSourceConnector"/>.
/// Verifies the placeholder returns a deterministic failure both when the
/// base URL is blank and when it is configured (the real MConnect wiring
/// lands in a follow-up iteration).
/// </summary>
public sealed class RspExternalSourceConnectorTests
{
    /// <summary>SourceCode literal is the canonical upper-case "RSP".</summary>
    [Fact]
    public void SourceCode_IsRsp()
    {
        var connector = new RspExternalSourceConnector(
            Options.Create(new ExternalSourceOptions()));

        connector.SourceCode.Should().Be("RSP");
    }

    /// <summary>Blank base URL returns EXT_SRC.RSP_NOT_CONFIGURED.</summary>
    [Fact]
    public async Task FetchAsync_BlankBaseUrl_ReturnsNotConfigured()
    {
        var connector = new RspExternalSourceConnector(
            Options.Create(new ExternalSourceOptions
            {
                Rsp = new ExternalSourceConnectorOptions { BaseUrl = string.Empty },
            }));

        var result = await connector.FetchAsync(new DateOnly(2026, 5, 24));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(RspExternalSourceConnector.NotConfiguredCode);
    }

    /// <summary>
    /// Even when a base URL is configured the placeholder still refuses to
    /// fetch — the real MConnect SOAP wiring lands in a follow-up iteration.
    /// </summary>
    [Fact]
    public async Task FetchAsync_ConfiguredBaseUrl_StillReturnsNotConfigured()
    {
        var connector = new RspExternalSourceConnector(
            Options.Create(new ExternalSourceOptions
            {
                Rsp = new ExternalSourceConnectorOptions
                {
                    BaseUrl = "https://mconnect.test.gov.md/rsp",
                },
            }));

        var result = await connector.FetchAsync(new DateOnly(2026, 5, 24));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(RspExternalSourceConnector.NotConfiguredCode);
        result.ErrorMessage.Should().Contain("placeholder");
    }
}
