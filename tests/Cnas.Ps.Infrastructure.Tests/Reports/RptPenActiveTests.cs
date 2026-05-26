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
/// Integration tests for the Annex 6 report <c>RPT-PEN-ACTIVE</c> — list of beneficiaries
/// of pensions active as of a given UTC date. Uses EF Core InMemory persistence and a
/// stubbed Sqid encoder so that test assertions can match against the deterministic
/// <c>sqid-{id}</c> form rather than rely on real Sqids alphabet/salt configuration.
/// </summary>
public class RptPenActiveTests
{
    /// <summary>Fixed UTC clock used by every harness so that test expectations are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-PEN-ACTIVE";

    /// <summary>
    /// Verifies the report's contract surface (code + headers in declared order). This locks
    /// the column shape so callers downstream (Excel templates, integration jobs) do not
    /// silently drift when columns are reordered or renamed.
    /// </summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedActiveAsync("2000000000001", "Ion Popescu", "SP-OLD-AGE", grantedFromUtc: ClockNow.AddYears(-1), closedAtUtc: null);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be(
            "Dossier Sqid,Beneficiary IDNP,Full Name,Service Code,Monthly Amount (MDL),Granted From (UTC)");
    }

    /// <summary>
    /// Seeds three dossiers (one active, one closed before <c>asOfUtc</c>, one granted in the
    /// future) and asserts that only the genuinely-active dossier appears. Also asserts that
    /// the <c>DossierSqid</c> column is Sqid-encoded (per CLAUDE.md RULE 3) — i.e. it does not
    /// parse as a long.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_ReturnsExpectedRows()
    {
        var harness = Harness.Create();
        // Active beneficiary granted last year, never closed — must appear.
        await harness.SeedActiveAsync(
            "2000000000001", "Ion Popescu", "SP-OLD-AGE",
            grantedFromUtc: ClockNow.AddYears(-1), closedAtUtc: null, monthlyAmount: 1234.56m);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("2000000000001");
        text.Should().Contain("Ion Popescu");
        text.Should().Contain("SP-OLD-AGE");
        text.Should().Contain("1234.56");

        var dataLine = text.Split("\r\n").First(l => l.Contains("Ion Popescu"));
        var sqid = dataLine.Split(',')[0];
        sqid.Should().NotBeNullOrWhiteSpace();
        long.TryParse(sqid, out _).Should().BeFalse(
            "DossierSqid must be Sqid-encoded — not a raw long.");
        sqid.Should().StartWith("sqid-");
    }

    /// <summary>
    /// Negative case — a dossier closed before <c>asOfUtc</c> must NOT appear, and a dossier
    /// granted strictly after <c>asOfUtc</c> must NOT appear.
    /// </summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesClosedAndFuture()
    {
        var harness = Harness.Create();
        // Closed last month — must not appear.
        await harness.SeedActiveAsync(
            "2000000000002", "Closed Person", "SP-CLOSED",
            grantedFromUtc: ClockNow.AddYears(-2), closedAtUtc: ClockNow.AddMonths(-1));
        // Granted in the future — must not appear.
        await harness.SeedActiveAsync(
            "2000000000003", "Future Person", "SP-FUTURE",
            grantedFromUtc: ClockNow.AddMonths(1), closedAtUtc: null);
        // Genuinely active baseline — must appear.
        await harness.SeedActiveAsync(
            "2000000000004", "Active Person", "SP-ACTIVE",
            grantedFromUtc: ClockNow.AddYears(-1), closedAtUtc: null);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("Active Person");
        text.Should().NotContain("Closed Person");
        text.Should().NotContain("Future Person");
    }

    /// <summary>
    /// Missing <c>asOfUtc</c> parameter must be rejected with a validation error so the caller
    /// cannot accidentally receive a snapshot of "everything ever granted".
    /// </summary>
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

    /// <summary>Stub <see cref="ICnasTimeProvider"/> that returns a fixed instant for deterministic tests.</summary>
    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        /// <inheritdoc />
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Composes the EF Core InMemory context + ReportingService with stubbed collaborators.</summary>
    private sealed class Harness
    {
        /// <summary>EF Core InMemory context — disposed by xUnit when the test exits.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>The system under test.</summary>
        public required ReportingService Service { get; init; }

        /// <summary>Builds a fresh harness with a per-test isolated InMemory database.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-pen-active-{Guid.NewGuid():N}")
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
        /// Seeds an Approved application + its Dossier with the supplied grant + closure dates,
        /// linked to a freshly-created passport whose <see cref="ServicePassport.Code"/> is the
        /// supplied <paramref name="passportCode"/>. The Solicitant's display name and IDNP are
        /// what the report's "Full Name" + "Beneficiary IDNP" columns echo back.
        /// </summary>
        public async Task SeedActiveAsync(
            string idnp,
            string fullName,
            string passportCode,
            DateTime grantedFromUtc,
            DateTime? closedAtUtc,
            decimal monthlyAmount = 500m)
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
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = ApplicationStatus.Approved,
                FormPayloadJson = "{}",
                SubmittedAtUtc = grantedFromUtc.AddDays(-7),
                IsActive = true,
            };
            Db.Applications.Add(application);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = grantedFromUtc,
                ApplicationId = application.Id,
                DossierNumber = $"D-{idnp[^4..]}",
                AcceptedAtUtc = grantedFromUtc,
                ClosedAtUtc = closedAtUtc,
                ComputedAmountMdl = monthlyAmount,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }
    }
}
