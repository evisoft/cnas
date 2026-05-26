using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports;

/// <summary>
/// Integration tests for the Annex 6h report <c>RPT-DOSSIER-EXAMINATION-DURATION</c> —
/// per-service-code average and nearest-rank p50 / p90 / p95 examination duration (in days),
/// where duration = <see cref="Dossier.ClosedAtUtc"/> − <see cref="Dossier.AcceptedAtUtc"/>.
/// Source: dossiers closed inside the half-open UTC window <c>[fromUtc, toUtc)</c> that also
/// have an <see cref="Dossier.AcceptedAtUtc"/> recorded.
/// </summary>
public class RptDossierExaminationDurationTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOSSIER-EXAMINATION-DURATION";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Service Code,Closed Count,Avg Days,P50 Days,P90 Days,P95 Days");
    }

    /// <summary>
    /// Seeds three closed dossiers under service <c>SP-A</c> with examination durations
    /// 2, 4 and 10 days (Avg = 5.33, nearest-rank P50 = 4, P90 = 10, P95 = 10), plus one
    /// dossier with no <see cref="Dossier.AcceptedAtUtc"/> (excluded) and one closed
    /// outside the window (excluded).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_AggregatesAvgAndPercentiles()
    {
        var harness = Harness.Create();
        // In-window closed dossiers for SP-A with examination durations 2, 4, 10 days.
        await harness.SeedClosedDossierAsync("SP-A",
            acceptedUtc: ClockNow.AddDays(-12), closedUtc: ClockNow.AddDays(-10)); // 2d
        await harness.SeedClosedDossierAsync("SP-A",
            acceptedUtc: ClockNow.AddDays(-14), closedUtc: ClockNow.AddDays(-10)); // 4d
        await harness.SeedClosedDossierAsync("SP-A",
            acceptedUtc: ClockNow.AddDays(-20), closedUtc: ClockNow.AddDays(-10)); // 10d
        // No AcceptedAtUtc — excluded (never entered examination phase).
        await harness.SeedClosedDossierAsync("SP-A",
            acceptedUtc: null, closedUtc: ClockNow.AddDays(-5));
        // Closed outside the window — excluded.
        await harness.SeedClosedDossierAsync("SP-A",
            acceptedUtc: ClockNow.AddDays(-100), closedUtc: ClockNow.AddDays(-95));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 1 service-code row.
        lines.Should().HaveCount(2);
        // Avg of (2, 4, 10) = 5.33; nearest-rank P50 over sorted [2,4,10] (ceil(0.5*3)=2 → index 1) = 4;
        // P90 → ceil(0.9*3)=3 → index 2 = 10; P95 → ceil(0.95*3)=3 → index 2 = 10.
        lines[1].Should().Be("SP-A,3,5.33,4.00,10.00,10.00");
    }

    /// <summary>An empty window emits only the header row.</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsOnlyHeader()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
    }

    /// <summary>Missing window parameters must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingParameters_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>Builds the parameters JSON for the [fromUtc, toUtc) half-open window.</summary>
    private static string BuildParams(DateTime fromUtc, DateTime toUtc)
        => $"{{ \"fromUtc\": \"{fromUtc.ToString("O", CultureInfo.InvariantCulture)}\", " +
           $"\"toUtc\": \"{toUtc.ToString("O", CultureInfo.InvariantCulture)}\" }}";

    /// <summary>Reads the full text of a stream using UTF-8 with BOM detection.</summary>
    private static string ReadAllText(Stream stream)
    {
        stream.Position = 0;
        using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return sr.ReadToEnd();
    }

    /// <summary>Deterministic stub clock.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Test harness composing EF Core InMemory + ReportingService.</summary>
    private sealed class Harness
    {
        /// <summary>The in-memory database context.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>The system under test.</summary>
        public required ReportingService Service { get; init; }

        /// <summary>Caches passport ids by code so the same passport is re-used across dossiers.</summary>
        private readonly Dictionary<string, long> _passports = new(StringComparer.Ordinal);

        /// <summary>Cached solicitant id so seeding doesn't churn rows.</summary>
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-examdur-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>
        /// Seeds a closed <see cref="Dossier"/> whose owning application is attached to a
        /// passport with the supplied <paramref name="serviceCode"/>. The dossier's
        /// <see cref="Dossier.AcceptedAtUtc"/> is the supplied value (or null) and
        /// <see cref="Dossier.ClosedAtUtc"/> is the supplied closed timestamp.
        /// </summary>
        public async Task SeedClosedDossierAsync(string serviceCode, DateTime? acceptedUtc, DateTime closedUtc)
        {
            if (!_passports.TryGetValue(serviceCode, out var passportId))
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = serviceCode,
                    NameRo = serviceCode, DescriptionRo = serviceCode,
                    FormSchemaJson = "{}", WorkflowCode = "WF",
                    MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
                };
                Db.ServicePassports.Add(passport);
                await Db.SaveChangesAsync();
                passportId = passport.Id;
                _passports[serviceCode] = passportId;
            }
            if (_solicitantId is null)
            {
                var nationalId = "2000000077777";
                var s = new Solicitant
                {
                    CreatedAtUtc = ClockNow,
                    NationalId = nationalId,
                    NationalIdHash = IdHashHelper.Hash(nationalId),
                    Kind = ApplicantKind.NaturalPerson,
                    DisplayName = "Test",
                    IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = acceptedUtc ?? closedUtc.AddDays(-1),
                SolicitantId = _solicitantId.Value,
                ServicePassportId = passportId,
                Status = ApplicationStatus.Closed,
                FormPayloadJson = "{}",
                SubmittedAtUtc = acceptedUtc ?? closedUtc.AddDays(-1),
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = acceptedUtc ?? closedUtc.AddDays(-1),
                ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                AcceptedAtUtc = acceptedUtc,
                ClosedAtUtc = closedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
