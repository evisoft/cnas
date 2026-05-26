using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Cnas.Ps.Infrastructure.Tests.MGov;

/// <summary>
/// Test-only <see cref="HttpMessageHandler"/> that records every outgoing
/// <see cref="HttpRequestMessage"/> and replies with the response produced by the
/// supplied <see cref="Func{T, TResult}"/>. Used to drive HTTP-backed MGov clients in
/// integration-style unit tests without touching the network.
/// </summary>
/// <remarks>
/// The MGov clients wrap each <see cref="HttpRequestMessage"/> in a <c>using</c> block
/// so by the time a test inspects <see cref="Captured"/>, the request body content is
/// already disposed. To work around that, the handler eagerly reads the body into the
/// <see cref="CapturedBodies"/> list before disposal, while still keeping the request
/// object around so headers and URI remain inspectable.
/// </remarks>
internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    /// <summary>
    /// Initialises the handler with a synchronous responder. The responder receives the
    /// request the client just sent and returns whatever canned <see cref="HttpResponseMessage"/>
    /// the test wants the client to see.
    /// </summary>
    /// <param name="respond">Function producing the canned upstream response.</param>
    public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond ?? throw new ArgumentNullException(nameof(respond));
    }

    /// <summary>Helper that builds a handler always returning the same status code.</summary>
    public static CapturingHandler AlwaysStatus(HttpStatusCode status) =>
        new(_ => new HttpResponseMessage(status));

    /// <summary>Every request that flowed through the handler, in order.</summary>
    public List<HttpRequestMessage> Captured { get; } = new();

    /// <summary>Per-request body snapshot taken before the client disposes the content.</summary>
    public List<string> CapturedBodies { get; } = new();

    /// <summary>Convenience accessor for the most recent captured request.</summary>
    public HttpRequestMessage Last => Captured[^1];

    /// <summary>Convenience accessor for the most recent captured body.</summary>
    public string LastBody => CapturedBodies[^1];

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Captured.Add(request);
        // Snapshot the body now — once SendAsync returns, the client disposes the request
        // and the underlying content stream is no longer readable.
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        CapturedBodies.Add(body);
        return _respond(request);
    }
}
