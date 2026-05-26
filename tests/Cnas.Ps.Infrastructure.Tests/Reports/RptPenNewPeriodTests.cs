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
/// Integration tests for the Annex 6 report <c>RPT-PEN-NEW-PERIOD</c> — newly granted
/// pensions in the half-open UTC window <c>[fromUtc, toUtc)</c>.
/// </summary>
public class RptPenNewPeriodTests
{
    /// <summary>Fixed UTC clock used by every harness.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-PEN-NEW-PERIOD";

    /// <summary>Verifies the report's header row in declared order.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedApprovedAsync(
            "2000000000001", "Ana Test", "SP-OLD", decisionUtc: ClockNow.AddDays(-10), amount: 1000m);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be(
            "Dossier Sqid,Beneficiary IDNP,Full Name,Service Code,Decision (UTC),Monthly Amount (MDL)");
    }

    /// <summary>Seeds three rows around a window and asserts only the in-window row appears.</summary>
    [Fact]
    public async Task Execute_WithSeededData_ReturnsExpectedRows()
    {
        var harness = Harness.Create();
        await harness.SeedApprovedAsync(
            "2000000000001", "Ana In", "SP-A",
            decisionUtc: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), amount: 750m);
        await harness.SeedApprovedAsync(
            "2000000000002", "Boris Before", "SP-B",
            decisionUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), amount: 750m);
        await harness.SeedApprovedAsync(
            "2000000000003", "Carla After", "SP-C",
            decisionUtc: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), amount: 750m);

        var paramsJson = BuildParams(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("Ana In");
        text.Should().NotContain("Boris Before");
        text.Should().NotContain("Carla After");
    }

    /// <summary>The half-open semantics excludes the exact <c>toUtc</c> boundary.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_HalfOpenWindow()
    {
        var harness = Harness.Create();
        var boundary = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await harness.SeedApprovedAsync(
            "2000000000004", "Boundary Person", "SP-B", decisionUtc: boundary, amount: 1m);

        var paramsJson = BuildParams(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            boundary);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().NotContain("Boundary Person",
            "decisionUtc equal to toUtc must be excluded (half-open window).");
    }

    /// <summary>Row's <c>DossierSqid</c> column is Sqid-encoded — never a raw long.</summary>
    [Fact]
    public async Task Execute_DossierSqidIsEncoded()
    {
        var harness = Harness.Create();
        await harness.SeedApprovedAsync(
            "2000000000010", "Sqid Subject", "SP-S",
            decisionUtc: ClockNow.AddDays(-5), amount: 99m);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var dataLine = text.Split("\r\n").First(l => l.Contains("Sqid Subject"));
        var sqid = dataLine.Split(',')[0];
        sqid.Should().StartWith("sqid-");
        long.TryParse(sqid, out _).Should().BeFalse();
    }

    /// <summary>Missing <c>fromUtc</c>/<c>toUtc</c> must be rejected.</summary>
    [Fact]
    public async Task Execute_MissingWindow_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>Builds a parameters JSON string for the report.</summary>
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

    /// <summary>Test harness assembling EF Core InMemory + ReportingService.</summary>
    private sealed class Harness
    {
        /// <summary>InMemory context for the test.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>The system under test.</summary>
        public required ReportingService Service { get; init; }

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-pen-new-{Guid.NewGuid():N}")
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
        /// Seeds an Approved application + its Dossier whose AcceptedAtUtc is taken as the
        /// "decision date" reported by the row.
        /// </summary>
        public async Task SeedApprovedAsync(
            string idnp,
            string fullName,
            string passportCode,
            DateTime decisionUtc,
            decimal amount)
        {
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = passportCode,
                NameRo = passportCode,
                DescriptionRo = passportCode,
                FormSchemaJson = "{}",
                WorkflowCode = "WF-PEN",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = idnp,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = fullName,
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var application = new ServiceApplication
            {
                CreatedAtUtc = decisionUtc.AddDays(-7),
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Approved,
                FormPayloadJson = "{}",
                SubmittedAtUtc = decisionUtc.AddDays(-7),
                IsActive = true,
            };
            Db.Applications.Add(application);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = decisionUtc,
                ApplicationId = application.Id,
                DossierNumber = $"D-{idnp[^4..]}",
                AcceptedAtUtc = decisionUtc,
                ComputedAmountMdl = amount,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }
    }
}
