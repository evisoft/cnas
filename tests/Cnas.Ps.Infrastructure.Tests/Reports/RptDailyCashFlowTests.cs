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
/// Integration tests for the Annex 6f report <c>RPT-DAILY-CASH-FLOW</c> — per-calendar-day
/// total monthly amounts (MDL) and distinct beneficiary count inside the UTC window
/// <c>[fromUtc, toUtc)</c>. For each day in the window the report sums
/// <see cref="Dossier.ComputedAmountMdl"/> across approved dossiers active that day, mirroring
/// the RPT-PEN-ACTIVE filter at the day boundary. The histogram is dense — every day in the
/// window is emitted even when no dossier is active that day.
/// </summary>
public class RptDailyCashFlowTests
{
    /// <summary>Fixed UTC clock — day anchors are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DAILY-CASH-FLOW";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        var fromUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc);

        var paramsJson = BuildParams(fromUtc, toUtc);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Date (UTC),Total Disbursed (MDL),Beneficiary Count");
    }

    /// <summary>
    /// One approved dossier (100 MDL) accepted before the window. Active on every day in
    /// [May 1, May 3) → each row reports 100.00 / 1.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_SumsActiveApprovedDossiers()
    {
        var harness = Harness.Create();
        await harness.SeedApprovedDossierAsync(
            acceptedUtc: new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc), amount: 100m);

        var fromUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(fromUtc, toUtc);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("2026-05-01,100.00,1");
        lines.Should().Contain("2026-05-02,100.00,1");
    }

    /// <summary>Every day in the window is emitted even when zero dossiers are active.</summary>
    [Fact]
    public async Task Execute_DenseHistogram_EmitsAllDaysEvenWhenEmpty()
    {
        var harness = Harness.Create();

        var fromUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(fromUtc, toUtc);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("2026-05-01,0.00,0");
        lines.Should().Contain("2026-05-02,0.00,0");
        lines.Should().Contain("2026-05-03,0.00,0");
    }

    /// <summary>Dossiers closed before a given day do not contribute to that day.</summary>
    [Fact]
    public async Task Execute_RespectsClosedBefore_ExcludesClosedDossiers()
    {
        var harness = Harness.Create();
        // Accepted 2026-04-20, closed 2026-05-02. Active on May 1, but NOT May 2 or May 3.
        await harness.SeedApprovedDossierAsync(
            acceptedUtc: new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc),
            amount: 100m,
            closedUtc: new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc));

        var fromUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(fromUtc, toUtc);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("2026-05-01,100.00,1");
        lines.Should().Contain("2026-05-02,0.00,0");
        lines.Should().Contain("2026-05-03,0.00,0");
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

    /// <summary>A window that does not span at least one full day must be rejected.</summary>
    [Fact]
    public async Task Execute_ZeroDayWindow_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var fromUtc = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        // toUtc equal to fromUtc — half-open window is empty.
        var paramsJson = BuildParams(fromUtc, fromUtc);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

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

        /// <summary>Cached scaffolding ids so seeding doesn't churn rows.</summary>
        private long? _passportId;
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-dailycash-{Guid.NewGuid():N}")
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
        /// Seeds an Approved Dossier with the supplied <paramref name="acceptedUtc"/>,
        /// <paramref name="amount"/>, and optional <paramref name="closedUtc"/>.
        /// </summary>
        public async Task SeedApprovedDossierAsync(DateTime acceptedUtc, decimal amount, DateTime? closedUtc = null)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-DCF",
                    NameRo = "Dcf", DescriptionRo = "Dcf",
                    FormSchemaJson = "{}", WorkflowCode = "WF",
                    MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
                };
                Db.ServicePassports.Add(passport);
                await Db.SaveChangesAsync();
                _passportId = passport.Id;
            }
            if (_solicitantId is null)
            {
                var s = new Solicitant
                {
                    CreatedAtUtc = ClockNow, NationalId = "2000000099999",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = acceptedUtc.AddDays(-3), SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value, Status = ApplicationStatus.Approved,
                FormPayloadJson = "{}", SubmittedAtUtc = acceptedUtc.AddDays(-3), IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = acceptedUtc.AddDays(-3), ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                AcceptedAtUtc = acceptedUtc,
                ComputedAmountMdl = amount,
                ClosedAtUtc = closedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
