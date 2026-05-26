using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Cnas.Ps.Application.Audit;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Services;


/// <summary>
/// Default <see cref="ISiemExporter"/> implementation — formats audit rows via
/// <see cref="CefFormatter"/> and writes the resulting CEF lines wrapped in RFC 5424
/// syslog headers to a UDP endpoint. R0190 / SEC 049.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transport.</b> Only UDP is implemented in this batch. <see cref="SiemTransport.Tcp"/>
/// and <see cref="SiemTransport.TcpTls"/> return
/// <see cref="ErrorCodes.Internal"/> immediately so misconfiguration surfaces as a clean
/// log line rather than a silent no-op. The TCP / TLS branches are reserved for a
/// follow-up batch — see <see cref="SiemExporterOptions"/> remarks.
/// </para>
/// <para>
/// <b>UDP delivery semantics.</b> UDP is fire-and-forget. A successful
/// <see cref="UdpClient.SendAsync(byte[], int, string, int)"/> means the OS accepted the
/// packet for transmission — it does NOT guarantee the SIEM received or ingested it.
/// This is the documented R0190 trade-off and matches every other syslog feed on the
/// market. The exporter treats an exception from <c>SendAsync</c> as a failure signal
/// so transient host-down conditions don't quietly advance the forwarder checkpoint.
/// </para>
/// <para>
/// <b>Endpoint parsing.</b> The <c>host:port</c> string is split on the LAST <c>:</c>
/// (so IPv6 literals like <c>[::1]:514</c> are handled correctly by users who supply
/// the bracketed form). Missing or unparseable port suffixes default to <c>514</c>
/// — the historical syslog port. This is a best-effort parser, not RFC 3986; operators
/// who need anything more exotic are expected to ship a clean string.
/// </para>
/// <para>
/// <b>PRI byte computation.</b> RFC 5424 specifies <c>PRI = facility * 8 + severity</c>
/// where <c>severity</c> is the syslog severity (0-7, distinct from the CEF severity).
/// We map our <see cref="AuditSeverity"/> directly to a syslog severity inside
/// <see cref="ComputePriority"/>; the CEF severity in the payload is separately
/// computed by <see cref="CefFormatter.MapSeverity"/>. The two scales overlap but are
/// not identical — keeping them adjacent in this file makes the dual mapping obvious to
/// reviewers.
/// </para>
/// </remarks>
public sealed class SyslogCefSiemExporter : ISiemExporter
{
    private readonly SiemExporterOptions _options;
    private readonly ICnasTimeProvider _clock;
    private readonly ILogger<SyslogCefSiemExporter> _logger;
    private readonly string _hostname;

    /// <summary>
    /// Constructs the exporter with its options snapshot, an injected clock for the
    /// syslog header timestamp, and a logger. The hostname is resolved eagerly from the
    /// options' override (or the process machine name) so every forwarded record carries
    /// a stable syslog header source field.
    /// </summary>
    /// <param name="options">Bound options snapshot from <see cref="SiemExporterOptions.SectionName"/>.</param>
    /// <param name="clock">UTC clock — used for the BSD-style syslog header timestamp.</param>
    /// <param name="logger">Structured logger used for transport-failure WARNs.</param>
    public SyslogCefSiemExporter(
        IOptions<SiemExporterOptions> options,
        ICnasTimeProvider clock,
        ILogger<SyslogCefSiemExporter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _hostname = string.IsNullOrWhiteSpace(_options.HostnameOverride)
            ? Environment.MachineName
            : _options.HostnameOverride!;
    }

    /// <inheritdoc />
    public async Task<Result> ForwardAsync(
        IReadOnlyList<AuditLog> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // Disabled-state short-circuit — must come BEFORE the transport branch so a
        // disabled-but-misconfigured (e.g. TCP without an implementation) exporter still
        // returns Success and the polling job stays a no-op.
        if (!_options.Enabled)
        {
            return Result.Success();
        }

        if (rows.Count == 0)
        {
            return Result.Success();
        }

        // TCP / TLS are deferred — return a clean failure so callers know the
        // checkpoint must NOT advance.
        if (_options.Transport != SiemTransport.Udp)
        {
            return Result.Failure(
                ErrorCodes.Internal,
                $"SIEM transport '{_options.Transport}' is not implemented in this batch (R0190 follow-up).");
        }

        var (host, port) = ParseEndpoint(_options.Endpoint);

        try
        {
            // One UdpClient per ForwardAsync call. UdpClient is cheap to construct (no
            // connect-time round-trip on UDP) and disposing it releases the ephemeral
            // local port immediately; pooling would not measurably help.
            using var udp = new UdpClient();

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Filter by configured minimum severity. The audit subsystem stores
                // every event; the SIEM typically wants only the ones at or above the
                // configured operator threshold.
                if ((int)row.Severity < (int)_options.MinSeverity)
                {
                    continue;
                }

                var cef = CefFormatter.Format(
                    row,
                    _options.VendorName,
                    _options.ProductName,
                    _options.ProductVersion);

                // RFC 5424 wraps each CEF line in a syslog header: <PRI>TIMESTAMP HOST CEF.
                // BSD-style timestamp ("MMM dd HH:mm:ss") is the most widely-supported
                // form across the SIEM market; RFC 3339 / 5424 ISO timestamps land in the
                // CEF payload's rt= field for higher-fidelity ingestion.
                var priority = ComputePriority(_options.FacilityCode, row.Severity);
                var syslogLine = string.Create(
                    CultureInfo.InvariantCulture,
                    $"<{priority}>{_clock.UtcNow:MMM dd HH:mm:ss} {_hostname} {cef}");

                var bytes = Encoding.UTF8.GetBytes(syslogLine);
                await udp.SendAsync(bytes, bytes.Length, host, port).ConfigureAwait(false);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            // Caller-initiated cancellation propagates unchanged.
            throw;
        }
        catch (Exception ex)
        {
            // Any other transport error: log + return failure. The forwarder job uses
            // this failure to keep the checkpoint pinned, so the same range retries on
            // the next iteration.
            _logger.LogWarning(
                ex,
                "SyslogCefSiemExporter failed to forward to {Host}:{Port}.",
                host,
                port);
            return Result.Failure(
                ErrorCodes.Internal,
                $"Syslog transport failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Splits a <c>host:port</c> string on the LAST <c>:</c> character so IPv6 literals
    /// in bracketed form (<c>[::1]:514</c>) parse correctly. Falls back to
    /// <c>("localhost", 514)</c> for empty input and to <c>514</c> for missing or
    /// unparseable port suffixes.
    /// </summary>
    /// <param name="endpoint">Endpoint string as configured on <see cref="SiemExporterOptions.Endpoint"/>.</param>
    /// <returns>A <c>(host, port)</c> tuple suitable for <see cref="UdpClient.SendAsync(byte[], int, string, int)"/>.</returns>
    internal static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return ("localhost", 514);
        }

        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0 || idx == endpoint.Length - 1)
        {
            // No colon found, or colon at start (e.g. ":514"), or colon at end
            // (e.g. "host:"). In all three cases we treat the input as host-only and
            // fall back to the default port.
            return (endpoint.TrimEnd(':'), 514);
        }

        var host = endpoint[..idx];
        var portText = endpoint[(idx + 1)..];
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            return (host, 514);
        }

        return (host, port);
    }

    /// <summary>
    /// Computes the RFC 5424 PRI byte. The <see cref="AuditSeverity"/> enum is mapped to
    /// a standard syslog severity (0-7) here — separately from
    /// <see cref="CefFormatter.MapSeverity"/> which maps to the CEF severity scale (0-10).
    /// </summary>
    /// <param name="facility">Site-configured facility code (typically 13 = log audit).</param>
    /// <param name="severity">Audit-subsystem severity of the row being forwarded.</param>
    /// <returns>The composed PRI value to render between <c>&lt;</c> and <c>&gt;</c>.</returns>
    internal static int ComputePriority(int facility, AuditSeverity severity)
    {
        // RFC 5424 §6.2.1 — syslog severity scale:
        //   0=Emergency 1=Alert 2=Critical 3=Error 4=Warning 5=Notice 6=Informational 7=Debug
        var syslogSeverity = severity switch
        {
            AuditSeverity.Information => 6,
            AuditSeverity.Notice => 5,
            AuditSeverity.Sensitive => 4,
            AuditSeverity.Critical => 2,
            _ => 6,
        };
        return (facility * 8) + syslogSeverity;
    }
}
