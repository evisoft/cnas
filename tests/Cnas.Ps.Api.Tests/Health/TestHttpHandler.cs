using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Cnas.Ps.Api.Tests.Health;

/// <summary>
/// Minimal test-only <see cref="HttpMessageHandler"/> for the health-check adapter tests.
/// Pattern mirrors <c>CapturingHandler</c> in the infrastructure test project (kept local
/// here so the API test project does not need to reference the infrastructure test project).
/// </summary>
internal sealed class TestHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    /// <summary>Initialises the handler with an async responder.</summary>
    public TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond ?? throw new ArgumentNullException(nameof(respond));
    }

    /// <summary>Convenience helper that always returns the same status code.</summary>
    public static TestHttpHandler AlwaysStatus(HttpStatusCode status)
        => new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    /// <summary>Convenience helper that always throws a transport-level exception.</summary>
    public static TestHttpHandler AlwaysThrow(Exception ex)
        => new((_, _) => Task.FromException<HttpResponseMessage>(ex));

    /// <summary>Every request observed by the handler, in order.</summary>
    public List<HttpRequestMessage> Captured { get; } = new();

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Captured.Add(request);
        return _respond(request, cancellationToken);
    }
}
