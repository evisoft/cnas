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
/// Integration tests for the Annex 6e report <c>RPT-SLAS-MISSED</c> — open dossiers whose
/// age strictly exceeds the supplied SLA in days. Each row carries the DossierSqid,
/// beneficiary IDNP, ServicePassport code, dossier <c>ReceivedAtUtc</c>, and integer
/// <c>DaysOpen</c>. Filter: <see cref="Dossier.ClosedAtUtc"/> is null and
/// <c>(now - CreatedAtUtc).TotalDays &gt; nDays</c>.
/// </summary>
public class RptSlasMissedTests
{
    /// <summary>Fixed UTC clock so DaysOpen is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-SLAS-MISSED";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedOpenDossierAsync("2000000099001", "SP-X",
            createdUtc: ClockNow.AddDays(-50));

        var result = await harness.Service.GenerateAsync(Code, """{ "nDays": 10 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be(
            "Dossier Sqid,Beneficiary IDNP,Service Code,Received (UTC),Days Open");
    }

    /// <summary>
    /// Seeds an over-SLA open dossier (must appear), an under-SLA open dossier (must be
    /// filtered out by age), and a closed dossier whose age would otherwise breach SLA
    /// (must be excluded because <see cref="Dossier.ClosedAtUtc"/> is set).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_ReturnsOnlyOverSlaOpenDossiers()
    {
        var harness = Harness.Create();
        // Over SLA (50d > 10), open.
        await harness.SeedOpenDossierAsync("2000000099010", "SP-A", ClockNow.AddDays(-50));
        // Under SLA (3d < 10), open.
        await harness.SeedOpenDossierAsync("2000000099020", "SP-A", ClockNow.AddDays(-3));
        // Over SLA but closed — must be excluded.
        await harness.SeedClosedDossierAsync("2000000099030", "SP-A",
            ClockNow.AddDays(-50), ClockNow.AddDays(-1));

        var result = await harness.Service.GenerateAsync(Code, """{ "nDays": 10 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("2000000099010", "over-SLA open dossiers must appear");
        text.Should().NotContain("2000000099020", "under-SLA dossiers are below threshold");
        text.Should().NotContain("2000000099030", "closed dossiers must be excluded");
    }

    /// <summary>A dossier exactly <c>nDays</c> old must NOT appear — the inequality is strict.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_StrictGreaterThan()
    {
        var harness = Harness.Create();
        await harness.SeedOpenDossierAsync("2000000099040", "SP-B", ClockNow.AddDays(-10));

        var result = await harness.Service.GenerateAsync(Code, """{ "nDays": 10 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().NotContain("2000000099040",
            "dossiers exactly N days old are below the strict > N threshold");
    }

    /// <summary><c>DossierSqid</c> is Sqid-encoded.</summary>
    [Fact]
    public async Task Execute_DossierSqidIsEncoded()
    {
        var harness = Harness.Create();
        await harness.SeedOpenDossierAsync("2000000099050", "SP-C", ClockNow.AddDays(-30));

        var result = await harness.Service.GenerateAsync(Code, """{ "nDays": 5 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var line = text.Split("\r\n").First(l => l.Contains("2000000099050"));
        var sqid = line.Split(',')[0];
        sqid.Should().StartWith("sqid-");
        long.TryParse(sqid, out _).Should().BeFalse();
    }

    /// <summary>Missing / non-positive <c>nDays</c> must yield a validation failure.</summary>
    [Fact]
    public async Task Execute_MissingNDays_ReturnsValidationFailed()
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

        /// <summary>Cached passport ids by code so the same passport is re-used across dossiers.</summary>
        private readonly Dictionary<string, long> _passports = new(StringComparer.Ordinal);

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-slas-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds an open dossier (no <see cref="Dossier.ClosedAtUtc"/>).</summary>
        public Task SeedOpenDossierAsync(string idnp, string serviceCode, DateTime createdUtc)
            => SeedAsync(idnp, serviceCode, createdUtc, closedAtUtc: null);

        /// <summary>Seeds a closed dossier (<see cref="Dossier.ClosedAtUtc"/> set).</summary>
        public Task SeedClosedDossierAsync(string idnp, string serviceCode, DateTime createdUtc, DateTime closedAtUtc)
            => SeedAsync(idnp, serviceCode, createdUtc, closedAtUtc);

        private async Task SeedAsync(string idnp, string serviceCode, DateTime createdUtc, DateTime? closedAtUtc)
        {
            if (!_passports.TryGetValue(serviceCode, out var passportId))
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = createdUtc,
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

            var solicitant = new Solicitant
            {
                CreatedAtUtc = createdUtc, NationalId = idnp,
                Kind = ApplicantKind.NaturalPerson, DisplayName = $"Beneficiary {idnp}", IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = createdUtc, SolicitantId = solicitant.Id,
                ServicePassportId = passportId, Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}", SubmittedAtUtc = createdUtc, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = createdUtc, ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                ClosedAtUtc = closedAtUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
