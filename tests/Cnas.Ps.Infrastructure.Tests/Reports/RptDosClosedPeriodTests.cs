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
/// Integration tests for the Annex 6 report <c>RPT-DOS-CLOSED-PERIOD</c> — dossiers closed
/// within the supplied <c>[fromUtc, toUtc)</c> half-open UTC window. Closure is anchored on
/// <see cref="Dossier.ClosedAtUtc"/>; final outcome derives from the underlying
/// <see cref="ServiceApplication.Status"/> (mapped to Approved / Rejected / Cancelled).
/// </summary>
public class RptDosClosedPeriodTests
{
    /// <summary>Fixed UTC clock so report generation is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOS-CLOSED-PERIOD";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedClosedAsync(
            "2000000000001", "SP-A", ApplicationStatus.Approved,
            closedAtUtc: new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc));

        var paramsJson = BuildParams(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be(
            "Dossier Sqid,Beneficiary IDNP,Service Code,Closed (UTC),Final Outcome");
    }

    /// <summary>
    /// Seeds three dossiers — one in window (Approved), one in window (Rejected), one out of
    /// window. Asserts only the two in-window rows appear with mapped outcomes.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_ReturnsExpectedRows()
    {
        var harness = Harness.Create();
        await harness.SeedClosedAsync(
            "2000000000011", "SP-A", ApplicationStatus.Approved,
            closedAtUtc: new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc));
        await harness.SeedClosedAsync(
            "2000000000022", "SP-B", ApplicationStatus.Rejected,
            closedAtUtc: new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc));
        await harness.SeedClosedAsync(
            "2000000000033", "SP-C", ApplicationStatus.Approved,
            closedAtUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var paramsJson = BuildParams(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("2000000000011");
        text.Should().Contain("Approved");
        text.Should().Contain("2000000000022");
        text.Should().Contain("Rejected");
        text.Should().NotContain("2000000000033", "out-of-window rows must be filtered");
    }

    /// <summary>
    /// A dossier closed exactly at the upper bound is excluded — the window is half-open
    /// <c>[fromUtc, toUtc)</c> matching the convention established by the other Annex 6 reports.
    /// </summary>
    [Fact]
    public async Task Execute_RespectsFilter_UpperBoundIsExclusive()
    {
        var harness = Harness.Create();
        var to = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        await harness.SeedClosedAsync(
            "2000000000044", "SP-EDGE", ApplicationStatus.Approved, closedAtUtc: to);

        var paramsJson = BuildParams(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), to);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().NotContain("2000000000044",
            "Rows closed exactly at toUtc must be excluded (half-open window).");
    }

    /// <summary><c>DossierSqid</c> is always Sqid-encoded — never a raw long.</summary>
    [Fact]
    public async Task Execute_DossierSqidIsEncoded()
    {
        var harness = Harness.Create();
        await harness.SeedClosedAsync(
            "2000000000055", "SP-X", ApplicationStatus.Approved,
            closedAtUtc: new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc));

        var paramsJson = BuildParams(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var dataLine = text.Split("\r\n").First(l => l.Contains("2000000000055"));
        var sqid = dataLine.Split(',')[0];
        sqid.Should().StartWith("sqid-");
        long.TryParse(sqid, out _).Should().BeFalse();
    }

    /// <summary>Missing window parameters must be rejected with a validation error.</summary>
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

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-closed-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Cached passport id per code so that re-using a code doesn't churn rows.</summary>
        private readonly Dictionary<string, long> _passportIds = new(StringComparer.Ordinal);

        /// <summary>
        /// Seeds a Dossier whose underlying Application has the supplied <paramref name="status"/>
        /// and whose <see cref="Dossier.ClosedAtUtc"/> is set to <paramref name="closedAtUtc"/>.
        /// </summary>
        public async Task SeedClosedAsync(string idnp, string passportCode, ApplicationStatus status, DateTime closedAtUtc)
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
                CreatedAtUtc = closedAtUtc.AddDays(-10),
                NationalId = idnp,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = idnp,
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = closedAtUtc.AddDays(-10),
                SolicitantId = solicitant.Id,
                ServicePassportId = passportId,
                Status = status,
                FormPayloadJson = "{}",
                SubmittedAtUtc = closedAtUtc.AddDays(-10),
                ClosedAtUtc = closedAtUtc,
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = closedAtUtc.AddDays(-9),
                ApplicationId = app.Id,
                DossierNumber = $"D-{idnp[^4..]}-{Guid.NewGuid().ToString("N")[..4]}",
                ClosedAtUtc = closedAtUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
