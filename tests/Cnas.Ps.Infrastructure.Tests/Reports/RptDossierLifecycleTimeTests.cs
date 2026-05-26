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
/// Integration tests for the Annex 6d report <c>RPT-DOSSIER-LIFECYCLE-TIME</c> — average and
/// median dossier processing time (in days) per service. Each row aggregates over dossiers
/// whose <see cref="Dossier.ClosedAtUtc"/> falls in the half-open UTC window
/// <c>[fromUtc, toUtc)</c>. Lifecycle = ClosedAtUtc − CreatedAtUtc.
/// </summary>
public class RptDossierLifecycleTimeTests
{
    /// <summary>Fixed UTC clock so window anchors are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOSSIER-LIFECYCLE-TIME";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedClosedDossierAsync("SP-A",
            createdUtc: ClockNow.AddDays(-20), closedUtc: ClockNow.AddDays(-10));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Service Code,Avg Lifecycle Days,Median Lifecycle Days,Closed Count");
    }

    /// <summary>
    /// Seeds three SP-A dossiers closed in window with lifecycle (5, 10, 30) days and one
    /// SP-B dossier with (7) days. SP-A avg = 15.00, median = 10.00, count = 3.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_ComputesAverageAndMedian()
    {
        var harness = Harness.Create();
        // SP-A: lifecycles 5, 10, 30 → avg 15, median 10
        await harness.SeedClosedDossierAsync("SP-A",
            ClockNow.AddDays(-20), ClockNow.AddDays(-15));   // 5 days
        await harness.SeedClosedDossierAsync("SP-A",
            ClockNow.AddDays(-25), ClockNow.AddDays(-15));   // 10 days
        await harness.SeedClosedDossierAsync("SP-A",
            ClockNow.AddDays(-50), ClockNow.AddDays(-20));   // 30 days

        // SP-B: lifecycle 7 → avg 7, median 7, count 1
        await harness.SeedClosedDossierAsync("SP-B",
            ClockNow.AddDays(-20), ClockNow.AddDays(-13));   // 7 days

        var paramsJson = BuildParams(ClockNow.AddDays(-60), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines.Should().Contain("SP-A,15.00,10.00,3");
        lines.Should().Contain("SP-B,7.00,7.00,1");
    }

    /// <summary>
    /// Even-count median = average of the two middle values. Four SP-A dossiers with
    /// lifecycles 2, 4, 8, 10 → median = (4+8)/2 = 6.00, avg = 6.00, count = 4.
    /// </summary>
    [Fact]
    public async Task Execute_EvenCount_MedianIsMeanOfMiddleTwo()
    {
        var harness = Harness.Create();
        await harness.SeedClosedDossierAsync("SP-A", ClockNow.AddDays(-12), ClockNow.AddDays(-10));  // 2
        await harness.SeedClosedDossierAsync("SP-A", ClockNow.AddDays(-14), ClockNow.AddDays(-10));  // 4
        await harness.SeedClosedDossierAsync("SP-A", ClockNow.AddDays(-18), ClockNow.AddDays(-10));  // 8
        await harness.SeedClosedDossierAsync("SP-A", ClockNow.AddDays(-20), ClockNow.AddDays(-10));  // 10

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("SP-A,6.00,6.00,4");
    }

    /// <summary>Dossiers closed outside the window must not contribute.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesOutOfWindow()
    {
        var harness = Harness.Create();
        await harness.SeedClosedDossierAsync("SP-A",
            ClockNow.AddDays(-20), ClockNow.AddDays(-10));   // in window
        await harness.SeedClosedDossierAsync("SP-A",
            ClockNow.AddDays(-200), ClockNow.AddDays(-180)); // out of window

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Only the in-window dossier (10 days) contributes.
        lines.Should().Contain("SP-A,10.00,10.00,1");
    }

    /// <summary>Missing window parameters must be rejected.</summary>
    [Fact]
    public async Task Execute_MissingWindow_ReturnsValidationFailed()
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
                .UseInMemoryDatabase($"cnas-rpt-lifecycle-{Guid.NewGuid():N}")
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
        /// Seeds a closed Dossier whose owning application is attached to a passport with the
        /// supplied <paramref name="serviceCode"/>.
        /// </summary>
        public async Task SeedClosedDossierAsync(string serviceCode, DateTime createdUtc, DateTime closedUtc)
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
                var s = new Solicitant
                {
                    CreatedAtUtc = ClockNow, NationalId = "2000000077777",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = createdUtc, SolicitantId = _solicitantId.Value,
                ServicePassportId = passportId, Status = ApplicationStatus.Closed,
                FormPayloadJson = "{}", SubmittedAtUtc = createdUtc, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = createdUtc, ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                ClosedAtUtc = closedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
