using System.Threading.Channels;
using Cnas.Ps.Application.Audit;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Bounded in-memory queue fronting <see cref="AuditService"/> writes. Decouples the
/// request hot path from the DB + MLog flush. SEC 044 / R0186.
/// </summary>
/// <remarks>
/// <para>
/// The queue is a single-consumer / multi-producer <see cref="Channel{T}"/> of capacity
/// <see cref="Capacity"/> (4096). Producers call <see cref="TryEnqueue"/> which is a
/// non-blocking <c>Writer.TryWrite</c> — when the queue is full the producer gets
/// <c>false</c> back, allowing <see cref="AuditService"/> to log loudly and return
/// <see cref="Core.Common.ErrorCodes.Internal"/> without wedging the request thread.
/// </para>
/// <para>
/// Registered as a singleton so a single channel instance is shared by every scoped
/// <see cref="AuditService"/> and the singleton <see cref="AuditDrainer"/> background
/// service. The channel itself is thread-safe.
/// </para>
/// <para>
/// Marked <c>public</c> because the DI container needs to see it; the surface area
/// callers actually exercise is intentionally narrow — <see cref="TryEnqueue"/> for
/// producers and <see cref="Reader"/> for the drainer, both <c>internal</c>.
/// </para>
/// <para>
/// R0188 moved <see cref="AuditEventRecord"/> to the Application layer (was previously
/// declared inline in this file as <c>internal</c>) so the audit-archive abstraction
/// can be expressed as a contract type. The queue's behaviour is unchanged; only the
/// record's namespace changed.
/// </para>
/// </remarks>
public sealed class AuditWriteQueue
{
    /// <summary>Bounded capacity of the in-memory channel.</summary>
    /// <remarks>
    /// 4096 is sized to absorb a multi-second hiccup of the DB / MLog pipeline at the
    /// expected steady-state throughput. The drainer flushes every ≤1s, so a backlog
    /// of 4096 represents tens of seconds of audit traffic — enough headroom for a
    /// brief stall without dropping records, but small enough that a sustained outage
    /// becomes loudly visible (via the <c>LogError</c> on overflow) rather than
    /// silently consuming heap.
    /// </remarks>
    public const int Capacity = 4096;

    private readonly Channel<AuditEventRecord> _channel;

    /// <summary>Creates a new bounded queue with capacity <see cref="Capacity"/>.</summary>
    public AuditWriteQueue()
    {
        _channel = Channel.CreateBounded<AuditEventRecord>(new BoundedChannelOptions(Capacity)
        {
            // Wait mode: TryWrite returns false (without blocking) when the channel is
            // full, so AuditService can log loudly and return Internal. We deliberately
            // do NOT use DropWrite / DropOldest / DropNewest — those modes make TryWrite
            // return true while silently discarding records, hiding the backlog from
            // ops.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Attempts to enqueue <paramref name="record"/>. Returns <c>true</c> if the queue
    /// accepted the record, <c>false</c> if it was full.
    /// </summary>
    /// <param name="record">Audit record produced by <see cref="AuditService"/>.</param>
    /// <returns><c>true</c> on success, <c>false</c> when the channel is at capacity.</returns>
    internal bool TryEnqueue(AuditEventRecord record) => _channel.Writer.TryWrite(record);

    /// <summary>Channel reader used by <see cref="AuditDrainer"/>.</summary>
    internal ChannelReader<AuditEventRecord> Reader => _channel.Reader;

    /// <summary>Signals graceful shutdown — no more writes will be enqueued.</summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
