using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Cnas.Ps.Infrastructure.Tests.MGov.Resilience;

/// <summary>
/// Test-only <see cref="HttpMessageHandler"/> that drives the resilience pipeline by
/// returning a programmed sequence of responses (or throwing programmed exceptions).
/// Each invocation pulls the next item from <see cref="_program"/>; when the program
/// is exhausted the last item is repeated indefinitely. Records the timestamp of every
/// invocation so timing-sensitive backoff tests can assert relative delays.
/// </summary>
/// <remarks>
/// Distinct from <see cref="MGov.CapturingHandler"/> in two ways:
/// <list type="bullet">
///   <item><description>Captures invocation timestamps so backoff assertions are
///   possible without subscribing to Polly's internal events.</description></item>
///   <item><description>Allows the program to mix responses and exceptions on the same
///   sequence (the retry layer needs to see both flavours of transient failure
///   surface as separate retries).</description></item>
/// </list>
/// </remarks>
internal sealed class ResilienceTestHandler : HttpMessageHandler
{
    /// <summary>One scripted action in the response program.</summary>
    private sealed class Step
    {
        public HttpStatusCode? Status { get; init; }
        public Func<Exception>? ExceptionFactory { get; init; }
        public TimeSpan? Delay { get; init; }
    }

    private readonly List<Step> _program;
    private int _index = -1;
    private readonly ConcurrentQueue<DateTimeOffset> _timestamps = new();

    /// <summary>
    /// Initialises the handler with an empty program. Callers populate the sequence via
    /// <see cref="EnqueueStatus"/>, <see cref="EnqueueException"/>, or
    /// <see cref="EnqueueDelay"/> before issuing requests.
    /// </summary>
    public ResilienceTestHandler()
    {
        _program = new List<Step>();
    }

    /// <summary>Total invocation count observed since construction.</summary>
    public int CallCount => _timestamps.Count;

    /// <summary>Wall-clock timestamps of every invocation, in order.</summary>
    public IReadOnlyList<DateTimeOffset> Timestamps => new List<DateTimeOffset>(_timestamps);

    /// <summary>Append a status-code response to the program.</summary>
    public ResilienceTestHandler EnqueueStatus(HttpStatusCode status)
    {
        _program.Add(new Step { Status = status });
        return this;
    }

    /// <summary>Append a thrown exception to the program.</summary>
    public ResilienceTestHandler EnqueueException(Func<Exception> factory)
    {
        _program.Add(new Step { ExceptionFactory = factory });
        return this;
    }

    /// <summary>
    /// Append a step that sleeps for <paramref name="delay"/> then returns
    /// <paramref name="finalStatus"/>. Used by the per-attempt-timeout test to drive a
    /// slow handler.
    /// </summary>
    public ResilienceTestHandler EnqueueDelay(TimeSpan delay, HttpStatusCode finalStatus)
    {
        _program.Add(new Step { Status = finalStatus, Delay = delay });
        return this;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _timestamps.Enqueue(DateTimeOffset.UtcNow);
        var idx = Interlocked.Increment(ref _index);
        // Repeat the last programmed step if the test fired more requests than steps.
        var step = _program.Count == 0
            ? new Step { Status = HttpStatusCode.OK }
            : _program[Math.Min(idx, _program.Count - 1)];

        if (step.Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (step.ExceptionFactory is not null)
        {
            throw step.ExceptionFactory();
        }
        return new HttpResponseMessage(step.Status ?? HttpStatusCode.OK);
    }
}
