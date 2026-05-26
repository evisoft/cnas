using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Application.Audit;

/// <summary>
/// One-shot forwarder that formats a batch of persisted <see cref="AuditLog"/> rows as
/// ArcSight CEF (Common Event Format) lines and writes them to a configured syslog
/// endpoint. R0190 / SEC 049.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract.</b> The implementation is responsible for:
/// <list type="bullet">
///   <item><description>Filtering rows by configured <c>MinSeverity</c> (rows below threshold are silently dropped — they do NOT count against success/failure).</description></item>
///   <item><description>Formatting each surviving row via <see cref="CefFormatter.Format"/>.</description></item>
///   <item><description>Wrapping each CEF line in a syslog header (RFC 5424 PRI + timestamp + hostname).</description></item>
///   <item><description>Writing the resulting bytes to the configured transport.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Return contract.</b> <see cref="Result.Success()"/> means "every row passed in
/// was either filtered out by min-severity OR successfully handed to the transport."
/// It does NOT guarantee the SIEM actually ingested the packet — UDP is lossy by
/// design. <see cref="Result.Failure(string, string)"/> with
/// <see cref="Cnas.Ps.Core.Common.ErrorCodes.Internal"/> means a transport-layer error
/// fired (UDP socket failure / TCP refused / unimplemented transport selected); the
/// caller MUST NOT advance any checkpoint when failure is returned.
/// </para>
/// <para>
/// <b>Idempotency.</b> The exporter holds no state across calls — the same batch passed
/// twice will be sent to the SIEM twice. The polling job (<c>SiemForwarderJob</c>) is
/// the layer that prevents re-emission via the <see cref="SiemForwarderState"/>
/// checkpoint.
/// </para>
/// <para>
/// <b>Disabled state.</b> When the implementation's options dictate forwarding is
/// disabled (<c>SiemExporterOptions.Enabled = false</c>), the call returns
/// <see cref="Result.Success()"/> immediately without consulting the network or the
/// row collection — the disabled state is a no-op success, not an error.
/// </para>
/// </remarks>
public interface ISiemExporter
{
    /// <summary>
    /// Formats and writes the supplied audit rows. Returns <see cref="Result.Success()"/>
    /// even when zero rows were forwarded (every row filtered by min-severity, or
    /// forwarding disabled); returns a failure with
    /// <see cref="Cnas.Ps.Core.Common.ErrorCodes.Internal"/> on a transport error so the
    /// caller knows not to advance the checkpoint.
    /// </summary>
    /// <param name="rows">Batch of audit rows to forward. May be empty; never <c>null</c>.</param>
    /// <param name="cancellationToken">Cancellation token observed during the transport calls.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> on a clean transport handoff (or no-op disabled
    /// state); failure with <see cref="Cnas.Ps.Core.Common.ErrorCodes.Internal"/> on
    /// any transport-layer error.
    /// </returns>
    Task<Result> ForwardAsync(IReadOnlyList<AuditLog> rows, CancellationToken cancellationToken = default);
}
