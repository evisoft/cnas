using System.Net.Http;

namespace Cnas.Ps.Api.Tests.Health;

/// <summary>
/// Minimal <see cref="IHttpClientFactory"/> that returns a single pre-built
/// <see cref="HttpClient"/> for every <c>CreateClient(name)</c> call. The health-check
/// adapters do not branch on the client name, so a single-client factory is enough.
/// </summary>
internal sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    private readonly HttpClient _client = client;

    /// <inheritdoc />
    public HttpClient CreateClient(string name) => _client;
}
