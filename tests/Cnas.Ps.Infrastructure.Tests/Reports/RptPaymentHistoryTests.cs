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
/// Integration tests for the Annex 6c report <c>RPT-PAYMENT-HISTORY</c> — month-by-month
/// payment history for a beneficiary identified by their dossier Sqid. In the absence of a
/// dedicated Payment entity, payments are deterministically synthesised from the dossier's
/// <see cref="Dossier.ComputedAmountMdl"/> and the number of months elapsed between
/// <see cref="Dossier.AcceptedAtUtc"/> (granted-from) and the clock's current UTC moment.
/// All synthesised rows carry status <c>Paid</c>.
/// </summary>
public class RptPaymentHistoryTests
{
    /// <summary>Fixed UTC clock so month-elapsed math is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-PAYMENT-HISTORY";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        var dossierSqid = await harness.SeedDossierAsync(
            grantedFromUtc: ClockNow.AddMonths(-3), amount: 500m);

        var paramsJson = $"{{ \"dossierSqid\": \"{dossierSqid}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Month (UTC),Amount (MDL),Status");
    }

    /// <summary>
    /// A dossier granted three months ago must produce exactly three rows (one per elapsed
    /// month), each at the configured amount and all status <c>Paid</c>.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_EmitsOneRowPerElapsedMonth()
    {
        var harness = Harness.Create();
        var dossierSqid = await harness.SeedDossierAsync(
            grantedFromUtc: ClockNow.AddMonths(-3), amount: 1234.50m);

        var paramsJson = $"{{ \"dossierSqid\": \"{dossierSqid}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 data rows
        lines.Length.Should().Be(4);
        text.Should().Contain("1234.50");
        text.Should().Contain("Paid");
    }

    /// <summary>A dossier granted in the future or this month must emit zero rows.</summary>
    [Fact]
    public async Task Execute_NoElapsedMonths_EmitsNoRows()
    {
        var harness = Harness.Create();
        var dossierSqid = await harness.SeedDossierAsync(
            grantedFromUtc: ClockNow.AddDays(-1), amount: 100m);

        var paramsJson = $"{{ \"dossierSqid\": \"{dossierSqid}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Header only — no months elapsed yet.
        lines.Length.Should().Be(1);
    }

    /// <summary>Missing <c>dossierSqid</c> parameter must be rejected.</summary>
    [Fact]
    public async Task Execute_MissingDossierSqid_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>An unrecognised dossier sqid must surface as a not-found failure.</summary>
    [Fact]
    public async Task Execute_UnknownDossier_ReturnsNotFound()
    {
        var harness = Harness.Create();
        var paramsJson = """{ "dossierSqid": "no-such-dossier" }""";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
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

        /// <summary>The Sqid service substitute — exposed so seeders can encode the dossier id.</summary>
        public required ISqidService Sqids { get; init; }

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-payhist-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            // Round-trip: anything matching "sqid-<long>" decodes to <long>; everything else fails.
            sqids.TryDecode(Arg.Any<string?>()).Returns(call =>
            {
                var s = call.Arg<string?>();
                if (s is not null && s.StartsWith("sqid-", StringComparison.Ordinal)
                    && long.TryParse(s.AsSpan(5), out var id))
                {
                    return Result<long>.Success(id);
                }
                return Result<long>.Failure(ErrorCodes.InvalidSqid, "bad sqid");
            });
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service, Sqids = sqids };
        }

        /// <summary>
        /// Seeds an approved active Dossier and returns its external Sqid for the caller to
        /// pass back into the report's parameters.
        /// </summary>
        public async Task<string> SeedDossierAsync(DateTime grantedFromUtc, decimal amount)
        {
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-PH",
                NameRo = "PH", DescriptionRo = "PH",
                FormSchemaJson = "{}", WorkflowCode = "WF",
                MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow, NationalId = "2000000077777",
                Kind = ApplicantKind.NaturalPerson, DisplayName = "Pensioner", IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = grantedFromUtc, SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id, Status = ApplicationStatus.Approved,
                FormPayloadJson = "{}", SubmittedAtUtc = grantedFromUtc, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = grantedFromUtc, ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                AcceptedAtUtc = grantedFromUtc,
                ComputedAmountMdl = amount,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            return Sqids.Encode(dossier.Id);
        }
    }
}
