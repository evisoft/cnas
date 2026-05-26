using Cnas.Ps.Core.Domain;

namespace Cnas.Ps.Infrastructure.Services;

/// <summary>
/// Configuration surface for the SIEM (Security Information and Event Management) audit
/// forwarder (R0190 / SEC 049). Bound from <c>Cnas:Audit:Siem</c>. All fields ship with
/// production-safe defaults so an operator can flip a single boolean
/// (<see cref="Enabled"/>) to wire the feed up to ArcSight / Splunk / QRadar / Elastic
/// SIEM / Wazuh.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why disabled by default.</b> Most environments do not have a SIEM until operators
/// deliberately stand one up; turning forwarding on by default would push CEF traffic at a
/// host that doesn't exist and fill log timelines with spurious WARN-level transport
/// failures. <see cref="Enabled"/> defaults to <c>false</c> so the chart ships safely and
/// the operator opts in explicitly.
/// </para>
/// <para>
/// <b>UDP-only in this batch.</b> <see cref="Transport"/> accepts the full RFC 5424
/// transport range (<see cref="SiemTransport.Udp"/>, <see cref="SiemTransport.Tcp"/>,
/// <see cref="SiemTransport.TcpTls"/>) at the contract level but only the UDP branch is
/// implemented — TCP and TCP/TLS return <see cref="Cnas.Ps.Core.Common.ErrorCodes.Internal"/>
/// from <c>SyslogCefSiemExporter</c> until the follow-up batch lands. UDP is the
/// historical syslog default and is what every mainstream SIEM ingests natively, so the
/// trade-off keeps the integration surface useful while we ship.
/// </para>
/// <para>
/// <b>Stable cron seam.</b> <see cref="Cron"/> is a Quartz cron expression (
/// <c>"0 */1 * * * ?"</c> = every minute on the second boundary). The QuartzComposition
/// wiring currently hard-codes the minute cadence; this field is the documented seam
/// for a future per-environment override and is kept on the options surface so the
/// contract is stable today even before the wiring catches up.
/// </para>
/// </remarks>
public sealed class SiemExporterOptions
{
    /// <summary>Configuration section name — <c>Cnas:Audit:Siem</c>.</summary>
    public const string SectionName = "Cnas:Audit:Siem";

    /// <summary>
    /// Master switch. When <c>false</c> (the default) the forwarder background job is a
    /// no-op and the exporter returns <c>Result.Success()</c> without touching the
    /// network — operators can leave the registration in place across every environment
    /// without an inadvertent dial-out.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Syslog endpoint in <c>host:port</c> form. The default <c>"localhost:514"</c>
    /// matches the standard syslog port and lets developers run a local UDP listener
    /// (<c>rsyslogd</c>, <c>syslog-ng</c>, a netcat-based sink, etc.) without further
    /// configuration. If the port suffix is omitted or unparseable, the exporter falls
    /// back to <c>514</c>.
    /// </summary>
    public string Endpoint { get; init; } = "localhost:514";

    /// <summary>
    /// Wire protocol. Defaults to <see cref="SiemTransport.Udp"/> — the historical
    /// syslog default and the only branch implemented in this batch. TCP and TCP/TLS
    /// are documented as future scope (see class remarks).
    /// </summary>
    public SiemTransport Transport { get; init; } = SiemTransport.Udp;

    /// <summary>
    /// Minimum <see cref="AuditSeverity"/> level forwarded. Rows whose
    /// <see cref="AuditLog.Severity"/> is strictly below this threshold are silently
    /// skipped without contributing to <c>cnas.audit.siem_forwarded</c>. Default is
    /// <see cref="AuditSeverity.Notice"/> so informational reads do not flood the SIEM;
    /// operators concerned about anti-tamper coverage may lower it to
    /// <see cref="AuditSeverity.Information"/> at the cost of higher ingest volume.
    /// </summary>
    public AuditSeverity MinSeverity { get; init; } = AuditSeverity.Notice;

    /// <summary>
    /// Quartz cron expression governing the polling cadence. Default
    /// <c>"0 */1 * * * ?"</c> = every minute on the second boundary. See class remarks
    /// for the wiring caveat.
    /// </summary>
    public string Cron { get; init; } = "0 */1 * * * ?";

    /// <summary>
    /// Maximum number of audit rows scanned per iteration. Bounds the DB scan so a
    /// pathological backlog (e.g. SIEM down for hours) cannot wedge a single fire for
    /// minutes at a time. Defaults to 500 — leaves the forwarder room to catch up after
    /// a sub-hour outage without bloating per-iteration memory.
    /// </summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// RFC 5424 syslog facility code embedded in the priority byte
    /// (<c>PRI = facility * 8 + severity</c>). Default <c>13</c> ("log audit") is the
    /// SIEM-conventional choice for audit feeds. Operators may override to align with
    /// site routing rules; values are intentionally not validated against the RFC range
    /// (0-23) because non-standard facilities are sometimes used by site-specific SIEM
    /// adapters.
    /// </summary>
    public int FacilityCode { get; init; } = 13;

    /// <summary>
    /// Optional override for the hostname field of the syslog header. When <c>null</c>
    /// (the default) the exporter uses <see cref="System.Environment.MachineName"/>;
    /// containerised deployments may want a stable name (e.g. the K8s pod name) so SIEM
    /// dashboards can group events by logical source rather than ephemeral container id.
    /// </summary>
    public string? HostnameOverride { get; init; }

    /// <summary>
    /// CEF vendor header field. Default <c>"CNAS"</c>. Exposed for the rare case where
    /// the SIEM has a parser tied to a non-default vendor string; most operators leave
    /// it alone.
    /// </summary>
    public string VendorName { get; init; } = "CNAS";

    /// <summary>
    /// CEF product header field. Default <c>"Cnas.Ps"</c> matches the project's stable
    /// identifier across logs and metrics.
    /// </summary>
    public string ProductName { get; init; } = "Cnas.Ps";

    /// <summary>
    /// CEF product-version header field. Default <c>"1.0"</c>; bump per release once the
    /// SIEM dashboard has a version pivot.
    /// </summary>
    public string ProductVersion { get; init; } = "1.0";
}

/// <summary>
/// Syslog wire protocol selector for <see cref="SiemExporterOptions.Transport"/>.
/// </summary>
/// <remarks>
/// Only <see cref="Udp"/> is implemented in the current batch — <see cref="Tcp"/> and
/// <see cref="TcpTls"/> are reserved for a future PR. The exporter's
/// <c>ForwardAsync</c> returns a Failure with <see cref="Cnas.Ps.Core.Common.ErrorCodes.Internal"/>
/// when an unimplemented transport is selected, so misconfiguration surfaces as a clear
/// transport-error log line rather than as a silent no-op.
/// </remarks>
public enum SiemTransport
{
    /// <summary>UDP datagrams (RFC 5424 default). Fire-and-forget, lossy in theory.</summary>
    Udp = 0,

    /// <summary>Plain TCP stream framing. Reserved for a future batch.</summary>
    Tcp = 1,

    /// <summary>TCP with TLS encryption (RFC 5425). Reserved for a future batch.</summary>
    TcpTls = 2,
}
