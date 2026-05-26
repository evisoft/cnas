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
/// Integration tests for the Annex 6 report <c>RPT-PAYMENT-BATCH-SUMMARY</c> — per-service
/// rollup of beneficiary count and total monthly payable amount (MDL) for active approved
/// dossiers as of a calendar month (first-of-month UTC anchor). Mirrors
/// <c>RPT-PEN-ACTIVE</c>'s active-as-of filter and groups by <see cref="ServicePassport.Code"/>.
/// </summary>
public class RptPaymentBatchSummaryTests
{
    /// <summary>Fixed UTC clock so report generation is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-PAYMENT-BATCH-SUMMARY";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedActiveAsync("SP-A", grantedFromUtc: ClockNow.AddMonths(-3),
            closedAtUtc: null, monthlyAmount: 500m);

        var paramsJson = """{ "monthUtc": "2026-05-01T00:00:00Z" }""";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Service Code,Beneficiary Count,Total Amount (MDL)");
    }

    /// <summary>
    /// Seeds three active beneficiaries (two on SP-A, one on SP-B). Verifies counts and
    /// summed amounts. Amounts are formatted with invariant-culture F2.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_RollsUpCountsAndAmounts()
    {
        var harness = Harness.Create();
        await harness.SeedActiveAsync("SP-A", grantedFromUtc: ClockNow.AddYears(-1),
            closedAtUtc: null, monthlyAmount: 1000m);
        await harness.SeedActiveAsync("SP-A", grantedFromUtc: ClockNow.AddYears(-2),
            closedAtUtc: null, monthlyAmount: 234.56m);
        await harness.SeedActiveAsync("SP-B", grantedFromUtc: ClockNow.AddMonths(-6),
            closedAtUtc: null, monthlyAmount: 750m);

        var paramsJson = """{ "monthUtc": "2026-05-01T00:00:00Z" }""";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("SP-A,2,1234.56");
        lines.Should().Contain("SP-B,1,750.00");
    }

    /// <summary>Dossiers closed before the target month must NOT contribute to the batch.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesClosedBeforeMonth()
    {
        var harness = Harness.Create();
        // Active beneficiary — must contribute.
        await harness.SeedActiveAsync("SP-IN", grantedFromUtc: ClockNow.AddYears(-1),
            closedAtUtc: null, monthlyAmount: 100m);
        // Closed before target month — must not contribute.
        await harness.SeedActiveAsync("SP-OUT", grantedFromUtc: ClockNow.AddYears(-2),
            closedAtUtc: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
            monthlyAmount: 999m);

        var paramsJson = """{ "monthUtc": "2026-05-15T12:34:56Z" }""";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("SP-IN,1,100.00");
        text.Should().NotContain("SP-OUT,");
    }

    /// <summary>Missing <c>monthUtc</c> must be rejected.</summary>
    [Fact]
    public async Task Execute_MissingMonth_ReturnsValidationFailed()
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

        /// <summary>Caches passport id per code so re-using a code doesn't churn rows.</summary>
        private readonly Dictionary<string, long> _passportIds = new(StringComparer.Ordinal);

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-batch-{Guid.NewGuid():N}")
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
        /// Seeds an Approved application + accepted Dossier with the supplied dates and
        /// monthly amount, linked to a passport with the supplied <paramref name="passportCode"/>.
        /// </summary>
        public async Task SeedActiveAsync(string passportCode, DateTime grantedFromUtc, DateTime? closedAtUtc, decimal monthlyAmount)
        {
            if (!_passportIds.TryGetValue(passportCode, out var passportId))
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = passportCode,
                    NameRo = passportCode,
                    DescriptionRo = passportCode,
                    FormSchemaJson = "{}",
                    WorkflowCode = "WF",
                    MaxProcessingDays = 30,
                    IsEnabled = true,
                    IsActive = true,
                };
                Db.ServicePassports.Add(passport);
                await Db.SaveChangesAsync();
                passportId = passport.Id;
                _passportIds[passportCode] = passportId;
            }

            var solicitant = new Solicitant
            {
                CreatedAtUtc = grantedFromUtc,
                NationalId = $"200{Guid.NewGuid().ToString("N")[..10]}",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Beneficiary",
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = grantedFromUtc,
                SolicitantId = solicitant.Id,
                ServicePassportId = passportId,
                Status = ApplicationStatus.Approved,
                FormPayloadJson = "{}",
                SubmittedAtUtc = grantedFromUtc.AddDays(-7),
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = grantedFromUtc,
                ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                AcceptedAtUtc = grantedFromUtc,
                ClosedAtUtc = closedAtUtc,
                ComputedAmountMdl = monthlyAmount,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
