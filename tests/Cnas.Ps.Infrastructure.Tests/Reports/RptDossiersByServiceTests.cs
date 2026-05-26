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
/// Integration tests for the Annex 6c report <c>RPT-DOSSIERS-BY-SERVICE</c> — distribution
/// of dossiers across services as of a supplied UTC moment. Each row carries the
/// <see cref="ServicePassport.Code"/>, its <see cref="ServicePassport.NameRo"/> (Romanian
/// title), and per-status counts for the open, approved, rejected, and closed dossiers
/// associated with that service. Dossiers are "existing at <c>asOfUtc</c>" when their
/// <see cref="AuditableEntity.CreatedAtUtc"/> is ≤ the supplied moment.
/// </summary>
public class RptDossiersByServiceTests
{
    /// <summary>Fixed UTC clock so the asOf anchor is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOSSIERS-BY-SERVICE";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedDossierAsync("SP-A", "Pensii A",
            createdUtc: ClockNow.AddDays(-5), status: ApplicationStatus.UnderExamination);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Service Code,Service Title (RO),Open,Approved,Rejected,Closed");
    }

    /// <summary>
    /// Seeds a mix of dossiers across two services with varying statuses. Verifies that
    /// each service appears with the right per-status counts.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_RollsUpCountsPerService()
    {
        var harness = Harness.Create();
        // SP-A: 2 open, 1 approved, 1 rejected, 1 closed
        await harness.SeedDossierAsync("SP-A", "Pensii A", ClockNow.AddDays(-10), ApplicationStatus.UnderExamination);
        await harness.SeedDossierAsync("SP-A", "Pensii A", ClockNow.AddDays(-9), ApplicationStatus.Submitted);
        await harness.SeedDossierAsync("SP-A", "Pensii A", ClockNow.AddDays(-8), ApplicationStatus.Approved);
        await harness.SeedDossierAsync("SP-A", "Pensii A", ClockNow.AddDays(-7), ApplicationStatus.Rejected);
        await harness.SeedDossierAsync("SP-A", "Pensii A", ClockNow.AddDays(-6), ApplicationStatus.Closed);
        // SP-B: 1 open
        await harness.SeedDossierAsync("SP-B", "Alocații B", ClockNow.AddDays(-5), ApplicationStatus.UnderExamination);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // SP-A: 2 open (UnderExamination + Submitted), 1 approved, 1 rejected, 1 closed
        lines.Should().Contain("SP-A,Pensii A,2,1,1,1");
        lines.Should().Contain("SP-B,Alocații B,1,0,0,0");
    }

    /// <summary>Dossiers created after <c>asOfUtc</c> must be excluded from all buckets.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesDossiersCreatedAfterAsOf()
    {
        var harness = Harness.Create();
        await harness.SeedDossierAsync("SP-C", "Indemnizații C",
            ClockNow.AddDays(-10), ApplicationStatus.UnderExamination);   // counted
        await harness.SeedDossierAsync("SP-C", "Indemnizații C",
            ClockNow.AddDays(+1), ApplicationStatus.UnderExamination);    // excluded — future

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("SP-C,Indemnizații C,1,0,0,0");
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
                .UseInMemoryDatabase($"cnas-rpt-dosserv-{Guid.NewGuid():N}")
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
        /// Seeds a Dossier whose owning <see cref="ServiceApplication"/> carries the supplied
        /// <paramref name="status"/>, attached to a passport identified by
        /// <paramref name="serviceCode"/> with the <paramref name="serviceTitleRo"/> RO title.
        /// </summary>
        public async Task SeedDossierAsync(
            string serviceCode,
            string serviceTitleRo,
            DateTime createdUtc,
            ApplicationStatus status)
        {
            if (!_passports.TryGetValue(serviceCode, out var passportId))
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = serviceCode,
                    NameRo = serviceTitleRo,
                    DescriptionRo = serviceTitleRo,
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000088888",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = createdUtc, SolicitantId = _solicitantId.Value,
                ServicePassportId = passportId, Status = status,
                FormPayloadJson = "{}", SubmittedAtUtc = createdUtc, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = createdUtc, ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16], IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
