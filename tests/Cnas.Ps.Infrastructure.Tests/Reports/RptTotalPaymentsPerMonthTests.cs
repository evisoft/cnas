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
/// Integration tests for the Annex 6e report <c>RPT-TOTAL-PAYMENTS-PER-MONTH</c> — month-by-month
/// total payments inside a UTC window <c>[fromUtc, toUtc)</c>. Each row aggregates active
/// approved dossiers at the first-of-month anchor: distinct beneficiary count and summed
/// monthly amount (MDL). The histogram is dense over every calendar month in the window,
/// even those with zero beneficiaries.
/// </summary>
public class RptTotalPaymentsPerMonthTests
{
    /// <summary>Fixed UTC clock — month anchors are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-TOTAL-PAYMENTS-PER-MONTH";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var paramsJson = BuildParams(fromUtc, toUtc);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Month (UTC),Beneficiary Count,Total Amount (MDL)");
    }

    /// <summary>
    /// Two approved dossiers (100 MDL + 250 MDL) granted before the window (Dec 2025);
    /// both are active at every month anchor in [Jan, Mar) so each row reports 2 / 350.00.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_SumsActiveApprovedDossiers()
    {
        var harness = Harness.Create();
        await harness.SeedApprovedDossierAsync(
            acceptedUtc: new DateTime(2025, 12, 5, 0, 0, 0, DateTimeKind.Utc), amount: 100m);
        await harness.SeedApprovedDossierAsync(
            acceptedUtc: new DateTime(2025, 12, 10, 0, 0, 0, DateTimeKind.Utc), amount: 250m);

        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(fromUtc, toUtc);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain(l => l.StartsWith("2026-01-01", StringComparison.Ordinal) && l.EndsWith(",2,350.00", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("2026-02-01", StringComparison.Ordinal) && l.EndsWith(",2,350.00", StringComparison.Ordinal));
    }

    /// <summary>All months in the window are emitted, even those with zero beneficiaries (dense).</summary>
    [Fact]
    public async Task Execute_DenseHistogram_EmitsAllMonthsEvenWhenEmpty()
    {
        var harness = Harness.Create();

        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(fromUtc, toUtc);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Three rows expected (Jan, Feb, Mar), all zero.
        lines.Should().Contain(l => l.StartsWith("2026-01-01", StringComparison.Ordinal) && l.EndsWith(",0,0.00", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("2026-02-01", StringComparison.Ordinal) && l.EndsWith(",0,0.00", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("2026-03-01", StringComparison.Ordinal) && l.EndsWith(",0,0.00", StringComparison.Ordinal));
    }

    /// <summary>Dossiers closed before a given month do not contribute to that month.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesClosedBeforeMonth()
    {
        var harness = Harness.Create();
        // Approved Dec 2025, closed mid-Feb 2026 → active at Jan and Feb anchors but NOT Mar.
        await harness.SeedApprovedDossierAsync(
            acceptedUtc: new DateTime(2025, 12, 5, 0, 0, 0, DateTimeKind.Utc),
            amount: 100m,
            closedUtc: new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc));

        var fromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(fromUtc, toUtc);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain(l => l.StartsWith("2026-01-01", StringComparison.Ordinal) && l.EndsWith(",1,100.00", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("2026-03-01", StringComparison.Ordinal) && l.EndsWith(",0,0.00", StringComparison.Ordinal));
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

        /// <summary>Cached scaffolding ids so seeding doesn't churn rows.</summary>
        private long? _passportId;
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-totalpay-{Guid.NewGuid():N}")
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
        /// Seeds an Approved Dossier (active, <see cref="ServiceApplication.Status"/> =
        /// <see cref="ApplicationStatus.Approved"/>) with the supplied AcceptedAtUtc, amount,
        /// and optional ClosedAtUtc.
        /// </summary>
        public async Task SeedApprovedDossierAsync(DateTime acceptedUtc, decimal amount, DateTime? closedUtc = null)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-TP",
                    NameRo = "Tp", DescriptionRo = "Tp",
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000022222",
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
