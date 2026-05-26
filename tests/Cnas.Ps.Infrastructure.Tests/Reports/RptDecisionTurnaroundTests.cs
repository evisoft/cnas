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
/// Integration tests for the Annex 6d report <c>RPT-DECISION-TURNAROUND</c> — distribution of
/// dossier decision turnaround times (<see cref="Dossier.AcceptedAtUtc"/> → final decision)
/// into five fixed buckets: <c>&lt;3d</c>, <c>3-7d</c>, <c>7-14d</c>, <c>14-30d</c>, <c>&gt;30d</c>.
/// All five buckets are always emitted, even when their count is zero.
/// </summary>
/// <remarks>
/// Decision date is the dossier's <see cref="Dossier.ClosedAtUtc"/> (filter window applies to
/// it). Turnaround days = (ClosedAtUtc − AcceptedAtUtc).TotalDays. Dossiers with no
/// <see cref="Dossier.AcceptedAtUtc"/> are excluded — there is no acceptance moment to anchor
/// the turnaround.
/// </remarks>
public class RptDecisionTurnaroundTests
{
    /// <summary>Fixed UTC clock so window anchors are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DECISION-TURNAROUND";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedDecisionAsync(
            acceptedUtc: ClockNow.AddDays(-10),
            closedUtc: ClockNow.AddDays(-9));   // 1 day → <3d bucket

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Turnaround Bucket,Count");
    }

    /// <summary>
    /// One dossier per bucket — verify each ends up in the correct row and the other rows are zero.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_DistributesIntoBuckets()
    {
        var harness = Harness.Create();
        // <3d (turnaround = 1d)
        await harness.SeedDecisionAsync(ClockNow.AddDays(-10), ClockNow.AddDays(-9));
        // 3-7d (turnaround = 5d)
        await harness.SeedDecisionAsync(ClockNow.AddDays(-12), ClockNow.AddDays(-7));
        // 7-14d (turnaround = 10d)
        await harness.SeedDecisionAsync(ClockNow.AddDays(-15), ClockNow.AddDays(-5));
        // 14-30d (turnaround = 20d)
        await harness.SeedDecisionAsync(ClockNow.AddDays(-25), ClockNow.AddDays(-5));
        // >30d (turnaround = 50d)
        await harness.SeedDecisionAsync(ClockNow.AddDays(-55), ClockNow.AddDays(-5));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines.Should().Contain("<3d,1");
        lines.Should().Contain("3-7d,1");
        lines.Should().Contain("7-14d,1");
        lines.Should().Contain("14-30d,1");
        lines.Should().Contain(">30d,1");
    }

    /// <summary>Zero-count buckets must still appear in the output (dense histogram).</summary>
    [Fact]
    public async Task Execute_EmitsAllFiveBucketsEvenWhenZero()
    {
        var harness = Harness.Create();
        // No data — every bucket is zero.
        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 5 bucket rows = 6 lines.
        lines.Should().HaveCount(6);
        lines.Should().Contain("<3d,0");
        lines.Should().Contain("3-7d,0");
        lines.Should().Contain("7-14d,0");
        lines.Should().Contain("14-30d,0");
        lines.Should().Contain(">30d,0");
    }

    /// <summary>Dossiers closed outside the window must not contribute.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesOutOfWindow()
    {
        var harness = Harness.Create();
        // In window — counts (1d → <3d).
        await harness.SeedDecisionAsync(ClockNow.AddDays(-10), ClockNow.AddDays(-9));
        // Out of window — excluded.
        await harness.SeedDecisionAsync(ClockNow.AddDays(-200), ClockNow.AddDays(-150));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("<3d,1");
        // The out-of-window dossier (>30d) doesn't bump that bucket.
        lines.Should().Contain(">30d,0");
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
                .UseInMemoryDatabase($"cnas-rpt-dec-turn-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a Dossier with the supplied AcceptedAtUtc/ClosedAtUtc pair.</summary>
        public async Task SeedDecisionAsync(DateTime acceptedUtc, DateTime closedUtc)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-DEC",
                    NameRo = "Dec", DescriptionRo = "Dec",
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000033333",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = acceptedUtc.AddDays(-1), SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value, Status = ApplicationStatus.Closed,
                FormPayloadJson = "{}", SubmittedAtUtc = acceptedUtc.AddDays(-1), IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = acceptedUtc,
                ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                AcceptedAtUtc = acceptedUtc,
                ClosedAtUtc = closedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
