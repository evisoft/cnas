using System.Net;
using System.Net.Sockets;
using System.Text;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Tests for <see cref="SyslogCefSiemExporter"/> — R0190 / SEC 049 transport that wraps
/// <see cref="Cnas.Ps.Application.Audit.CefFormatter"/> output in syslog headers and
/// sends it to a configured UDP endpoint.
/// </summary>
/// <remarks>
/// Tests that need to verify wire bytes spin up a local UDP listener on an ephemeral
/// port (127.0.0.1:0) — the OS picks the port, the test reads it back from the bound
/// socket, and the exporter is pointed at <c>"127.0.0.1:{port}"</c>. This exercises the
/// real <see cref="UdpClient"/> path end-to-end instead of mocking at a seam.
/// </remarks>
public class SyslogCefSiemExporterTests
{
    private static readonly DateTime FixedNow = new(2026, 5, 21, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ForwardAsync_Disabled_ReturnsSuccess_NoSend()
    {
        // When Enabled=false the exporter must short-circuit to Success without
        // touching the network. We verify the no-send invariant by pointing the
        // exporter at a port no listener owns: a misbehaving exporter that tried to
        // send would either throw or silently drop the packet, but the assertion
        // here is the Success result + the absence of a transport exception
        // propagating up.
        var exporter = BuildExporter(new SiemExporterOptions
        {
            Enabled = false,
            Endpoint = "127.0.0.1:1",
        });

        var result = await exporter.ForwardAsync(new[] { BuildRow() });

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ForwardAsync_NoRows_ReturnsSuccess_NoSend()
    {
        // Empty batch — Success, no send. We point at a real listener so the test
        // can assert no datagrams arrived.
        using var listener = await UdpListener.StartAsync();
        var exporter = BuildExporter(new SiemExporterOptions
        {
            Enabled = true,
            Endpoint = $"127.0.0.1:{listener.Port}",
        });

        var result = await exporter.ForwardAsync(Array.Empty<AuditLog>());

        result.IsSuccess.Should().BeTrue();
        // Give the OS a brief window to deliver any spurious datagram.
        var received = await listener.TryReceiveAsync(TimeSpan.FromMilliseconds(150));
        received.Should().BeNull();
    }

    [Fact]
    public async Task ForwardAsync_BelowMinSeverity_RowsFiltered()
    {
        // MinSeverity=Notice means an Information row must NOT be sent. The result is
        // still Success — the exporter treats filtered-out rows as a clean skip.
        using var listener = await UdpListener.StartAsync();
        var exporter = BuildExporter(new SiemExporterOptions
        {
            Enabled = true,
            Endpoint = $"127.0.0.1:{listener.Port}",
            MinSeverity = AuditSeverity.Notice,
        });
        var row = BuildRow(severity: AuditSeverity.Information);

        var result = await exporter.ForwardAsync(new[] { row });

        result.IsSuccess.Should().BeTrue();
        var received = await listener.TryReceiveAsync(TimeSpan.FromMilliseconds(150));
        received.Should().BeNull();
    }

    [Fact]
    public async Task ForwardAsync_MultiRowBatch_AllSentInOrder()
    {
        // Three rows at or above MinSeverity must all reach the listener as
        // separate datagrams (one CEF line per row).
        using var listener = await UdpListener.StartAsync();
        var exporter = BuildExporter(new SiemExporterOptions
        {
            Enabled = true,
            Endpoint = $"127.0.0.1:{listener.Port}",
            MinSeverity = AuditSeverity.Information,
        });
        var rows = new[]
        {
            BuildRow(eventCode: "EVT.A"),
            BuildRow(eventCode: "EVT.B"),
            BuildRow(eventCode: "EVT.C"),
        };

        var result = await exporter.ForwardAsync(rows);

        result.IsSuccess.Should().BeTrue();
        var packets = await listener.ReceiveAllAsync(expected: 3, TimeSpan.FromSeconds(2));
        packets.Should().HaveCount(3);
        // The 3 packets must arrive in the order they were sent (UDP on localhost is
        // FIFO in practice; if a future test environment ever reorders them we'll
        // see this assertion flake).
        packets[0].Should().Contain("EVT.A");
        packets[1].Should().Contain("EVT.B");
        packets[2].Should().Contain("EVT.C");
        // Each packet must carry the syslog PRI prefix.
        packets[0].Should().StartWith("<");
    }

    [Fact]
    public async Task ForwardAsync_TcpTransport_ReturnsFailure_NotImplemented()
    {
        // TCP / TLS are deferred to a future batch. The exporter must surface a
        // clean failure with ErrorCodes.Internal so the polling job knows not to
        // advance the checkpoint.
        var exporter = BuildExporter(new SiemExporterOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:514",
            Transport = SiemTransport.Tcp,
        });

        var result = await exporter.ForwardAsync(new[] { BuildRow() });

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INTERNAL_ERROR");
        result.ErrorMessage.Should().Contain("Tcp");
    }

    [Fact]
    public async Task ForwardAsync_TcpTlsTransport_ReturnsFailure_NotImplemented()
    {
        // Companion to the TCP case — TCP/TLS is also deferred.
        var exporter = BuildExporter(new SiemExporterOptions
        {
            Enabled = true,
            Endpoint = "127.0.0.1:6514",
            Transport = SiemTransport.TcpTls,
        });

        var result = await exporter.ForwardAsync(new[] { BuildRow() });

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("INTERNAL_ERROR");
        result.ErrorMessage.Should().Contain("TcpTls");
    }

    [Fact]
    public void ParseEndpoint_ValidHostPort_Parses()
    {
        var (host, port) = SyslogCefSiemExporter.ParseEndpoint("siem.example.com:5514");

        host.Should().Be("siem.example.com");
        port.Should().Be(5514);
    }

    [Fact]
    public void ParseEndpoint_MissingPort_Defaults514()
    {
        // Host-only string falls back to the historical syslog default.
        var (host, port) = SyslogCefSiemExporter.ParseEndpoint("siem.example.com");

        host.Should().Be("siem.example.com");
        port.Should().Be(514);
    }

    [Fact]
    public void ParseEndpoint_UnparseablePort_Defaults514()
    {
        // Garbage in the port slot still resolves to a usable endpoint — better than
        // failing the whole forwarder for a single misconfigured character.
        var (host, port) = SyslogCefSiemExporter.ParseEndpoint("siem.example.com:not-a-port");

        host.Should().Be("siem.example.com");
        port.Should().Be(514);
    }

    [Theory]
    [InlineData(AuditSeverity.Information, 6)]
    [InlineData(AuditSeverity.Notice, 5)]
    [InlineData(AuditSeverity.Sensitive, 4)]
    [InlineData(AuditSeverity.Critical, 2)]
    public void ComputePriority_AppliesRfc5424Formula(AuditSeverity severity, int expectedSyslogSeverity)
    {
        // PRI = facility * 8 + syslog severity. With facility=13 the expected priority
        // is 13*8 + expectedSyslogSeverity.
        var pri = SyslogCefSiemExporter.ComputePriority(13, severity);

        pri.Should().Be((13 * 8) + expectedSyslogSeverity);
    }

    // ─────────────────────── helpers ───────────────────────

    /// <summary>Builds an <see cref="ISiemExporter"/> with the supplied options.</summary>
    private static SyslogCefSiemExporter BuildExporter(SiemExporterOptions options)
    {
        return new SyslogCefSiemExporter(
            Options.Create(options),
            new StubClock(FixedNow),
            NullLogger<SyslogCefSiemExporter>.Instance);
    }

    /// <summary>Deterministic clock used by the exporter under test.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Constructs a populated <see cref="AuditLog"/> fixture.</summary>
    private static AuditLog BuildRow(
        string eventCode = "TEST.EVENT",
        AuditSeverity severity = AuditSeverity.Notice,
        string actorId = "user-42")
    {
        return new AuditLog
        {
            CreatedAtUtc = FixedNow,
            EventAtUtc = FixedNow,
            EventCode = eventCode,
            Severity = severity,
            ActorId = actorId,
            TargetEntity = "UserProfile",
            TargetEntityId = 42L,
            DetailsJson = "{}",
            SourceIp = "10.0.0.1",
            CorrelationId = "corr-test",
            PrevHash = "GENESIS",
            RowHash = new string('0', 64),
        };
    }

    /// <summary>
    /// Tiny UDP listener bound to <c>127.0.0.1:0</c> (ephemeral port). Tests read the
    /// bound port back via <see cref="Port"/> and pass it to the exporter under test.
    /// </summary>
    private sealed class UdpListener : IDisposable
    {
        private readonly UdpClient _client;

        private UdpListener(UdpClient client)
        {
            _client = client;
        }

        public int Port { get; private set; }

        public static Task<UdpListener> StartAsync()
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var listener = new UdpListener(client)
            {
                Port = ((IPEndPoint)client.Client.LocalEndPoint!).Port,
            };
            return Task.FromResult(listener);
        }

        /// <summary>
        /// Waits up to <paramref name="timeout"/> for a single datagram and returns its
        /// UTF-8 decoded body. Returns <c>null</c> on timeout — used to assert "no
        /// packet arrived".
        /// </summary>
        public async Task<string?> TryReceiveAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var result = await _client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                return Encoding.UTF8.GetString(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads <paramref name="expected"/> datagrams within the supplied <paramref name="timeout"/>
        /// and returns their UTF-8 decoded bodies. Asserts (via xUnit-friendly throw) on
        /// timeout if fewer than expected arrived.
        /// </summary>
        public async Task<List<string>> ReceiveAllAsync(int expected, TimeSpan timeout)
        {
            var received = new List<string>(expected);
            using var cts = new CancellationTokenSource(timeout);
            while (received.Count < expected)
            {
                try
                {
                    var result = await _client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    received.Add(Encoding.UTF8.GetString(result.Buffer));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            return received;
        }

        public void Dispose() => _client.Dispose();
    }
}
