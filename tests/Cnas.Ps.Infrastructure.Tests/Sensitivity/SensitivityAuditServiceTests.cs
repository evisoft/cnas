using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cnas.Ps.Application.Sensitivity;
using Cnas.Ps.Application.UseCases;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Observability;
using Cnas.Ps.Infrastructure.Security;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Sensitivity;

/// <summary>
/// R0228 / TOR SEC 033 — verifies that <see cref="SensitivityAuditService"/> writes a
/// Sensitive <c>SENSITIVITY.RESTRICTED_ACCESS</c> audit row and increments the
/// <c>cnas.sensitivity.restricted_access</c> counter tagged with the resource.
/// </summary>
public sealed class SensitivityAuditServiceTests
{
    /// <summary>Shared field list — extracted into a static to satisfy CA1861.</summary>
    private static readonly string[] DisclosedFields = ["Idnp", "IdnpHash"];

    [Fact]
    public async Task RecordRestrictedAccessAsync_WritesSensitiveAuditRow_AndCountsByResource()
    {
        var audit = Substitute.For<IAuditService>();
        audit.RecordAsync(
                Arg.Any<string>(),
                Arg.Any<AuditSeverity>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<long?>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        using var capture = new SingleCounterCapture("cnas.sensitivity.restricted_access");

        var sut = new SensitivityAuditService(audit);

        await sut.RecordRestrictedAccessAsync(
            resource: "Solicitants",
            recordSqid: "k3Gq9",
            propertyNames: DisclosedFields,
            ct: CancellationToken.None);

        // Exactly ONE audit write at Sensitive severity carrying the canonical event code
        // and a JSON payload that names every disclosed Restricted field for forensics.
        await audit.Received(1).RecordAsync(
            "SENSITIVITY.RESTRICTED_ACCESS",
            AuditSeverity.Sensitive,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Is<string>(json =>
                json.Contains("Solicitants", System.StringComparison.Ordinal) &&
                json.Contains("Idnp", System.StringComparison.Ordinal) &&
                json.Contains("IdnpHash", System.StringComparison.Ordinal)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        capture.TotalIncrement.Should().Be(1);
        capture.Measurements.Should().ContainSingle()
            .Which.Tags.Should().Contain(t => t.Key == "resource" && (string?)t.Value == "Solicitants");
    }

    /// <summary>
    /// <see cref="MeterListener"/>-based capture for a single instrument name on
    /// <see cref="CnasMeter.MeterName"/>. Mirrors the pattern in
    /// <c>GridExporterTests.MetricCapture</c>.
    /// </summary>
    private sealed class SingleCounterCapture : System.IDisposable
    {
        private readonly MeterListener _listener;
        private readonly System.Collections.Generic.List<Measurement> _measurements = new();
        private readonly object _gate = new();

        public System.Collections.Generic.IReadOnlyList<Measurement> Measurements
        {
            get { lock (_gate) return _measurements.ToList(); }
        }

        public long TotalIncrement
        {
            get { lock (_gate) return _measurements.Sum(m => m.Value); }
        }

        public SingleCounterCapture(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CnasMeter.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                lock (_gate)
                {
                    _measurements.Add(new Measurement(value, tags.ToArray()));
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        public sealed record Measurement(
            long Value,
            System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, object?>> Tags);
    }
}
