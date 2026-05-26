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
/// Integration tests for the Annex 6d report <c>RPT-OUTSTANDING-AMOUNTS</c> — cumulative
/// outstanding amounts (MDL) per service, summed across active approved dossiers as of a
/// supplied UTC moment. Filter mirrors <c>RPT-PEN-ACTIVE</c>: <c>AcceptedAtUtc ≤ asOf</c>
/// AND (<c>ClosedAtUtc IS NULL</c> OR <c>ClosedAtUtc &gt; asOf</c>).
/// </summary>
public class RptOutstandingAmountsTests
{
    /// <summary>Fixed UTC clock so asOf anchors are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-OUTSTANDING-AMOUNTS";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedActiveAsync("SP-A", ClockNow.AddDays(-10), closedUtc: null, amount: 500m);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow.ToString("O", CultureInfo.InvariantCulture)}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Service Code,Beneficiary Count,Total Outstanding (MDL)");
    }

    /// <summary>Aggregates per-service beneficiary count and total outstanding amount.</summary>
    [Fact]
    public async Task Execute_WithSeededData_AggregatesPerService()
    {
        var harness = Harness.Create();
        // SP-A — 3 active beneficiaries, 500 + 750.50 + 200 = 1450.50
        await harness.SeedActiveAsync("SP-A", ClockNow.AddDays(-10), closedUtc: null, amount: 500m);
        await harness.SeedActiveAsync("SP-A", ClockNow.AddDays(-9), closedUtc: null, amount: 750.50m);
        await harness.SeedActiveAsync("SP-A", ClockNow.AddDays(-8), closedUtc: null, amount: 200m);
        // SP-B — 1 active beneficiary, 1000
        await harness.SeedActiveAsync("SP-B", ClockNow.AddDays(-7), closedUtc: null, amount: 1000m);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow.ToString("O", CultureInfo.InvariantCulture)}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("SP-A,3,1450.50");
        lines.Should().Contain("SP-B,1,1000.00");
    }

    /// <summary>Dossiers closed before asOf are excluded; null-closed dossiers are included.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesClosed()
    {
        var harness = Harness.Create();
        await harness.SeedActiveAsync("SP-C", ClockNow.AddDays(-10), closedUtc: null, amount: 500m);
        await harness.SeedActiveAsync("SP-C", ClockNow.AddDays(-20),
            closedUtc: ClockNow.AddDays(-5), amount: 9999m);   // closed before asOf — excluded

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow.ToString("O", CultureInfo.InvariantCulture)}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("SP-C,1,500.00");
        text.Should().NotContain("9999");
    }

    /// <summary>Dossiers with no granted (AcceptedAtUtc null) are excluded.</summary>
    [Fact]
    public async Task Execute_ExcludesDossiersNeverGranted()
    {
        var harness = Harness.Create();
        await harness.SeedActiveAsync("SP-D", ClockNow.AddDays(-10), closedUtc: null, amount: 500m);
        await harness.SeedActiveAsync("SP-D", grantedFromUtc: null, closedUtc: null, amount: 8888m);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow.ToString("O", CultureInfo.InvariantCulture)}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("SP-D,1,500.00");
        text.Should().NotContain("8888");
    }

    /// <summary>Missing <c>asOfUtc</c> must be rejected.</summary>
    [Fact]
    public async Task Execute_MissingAsOfUtc_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

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
                .UseInMemoryDatabase($"cnas-rpt-outstanding-{Guid.NewGuid():N}")
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
        /// Seeds an Approved application + a Dossier with the supplied grant and closure
        /// timestamps, computed amount in MDL, and passport identified by <paramref name="serviceCode"/>.
        /// </summary>
        public async Task SeedActiveAsync(
            string serviceCode,
            DateTime? grantedFromUtc,
            DateTime? closedUtc,
            decimal amount)
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000044444",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow.AddDays(-40), SolicitantId = _solicitantId.Value,
                ServicePassportId = passportId, Status = ApplicationStatus.Approved,
                FormPayloadJson = "{}", SubmittedAtUtc = ClockNow.AddDays(-40), IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = grantedFromUtc ?? ClockNow.AddDays(-30),
                ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                AcceptedAtUtc = grantedFromUtc,
                ClosedAtUtc = closedUtc,
                ComputedAmountMdl = amount,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
