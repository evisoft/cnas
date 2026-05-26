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
/// Integration tests for the Annex 6d report <c>RPT-NEW-APPLICATIONS-DAILY</c> — count of
/// newly-submitted applications per calendar day inside a UTC window. The histogram is
/// dense — every day in the window <c>[fromUtc.Date, toUtc.Date)</c> is emitted, even when
/// the count is zero.
/// </summary>
public class RptNewApplicationsDailyTests
{
    /// <summary>Fixed UTC clock (10:00) — date math anchors on the date component only.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-NEW-APPLICATIONS-DAILY";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync(ClockNow.AddDays(-1));

        var from = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(from, to);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Date,Count");
    }

    /// <summary>
    /// Seeds two applications on 2026-05-18 and one on 2026-05-19. The 2-day window
    /// 2026-05-18 → 2026-05-20 (exclusive) emits two rows: 18/2 and 19/1.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_CountsPerCalendarDay()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync(new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Utc));
        await harness.SeedApplicationAsync(new DateTime(2026, 5, 18, 14, 0, 0, DateTimeKind.Utc));
        await harness.SeedApplicationAsync(new DateTime(2026, 5, 19, 11, 0, 0, DateTimeKind.Utc));

        var from = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(from, to);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines.Should().Contain("2026-05-18,2");
        lines.Should().Contain("2026-05-19,1");
    }

    /// <summary>Days with no applications still emit a zero-count row.</summary>
    [Fact]
    public async Task Execute_EmitsZeroDays()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync(new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Utc));

        var from = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 21, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(from, to);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Three day rows (18/19/20) + 1 header = 4 lines, two of them zero.
        lines.Should().HaveCount(4);
        lines.Should().Contain("2026-05-18,1");
        lines.Should().Contain("2026-05-19,0");
        lines.Should().Contain("2026-05-20,0");
    }

    /// <summary>Applications outside the window are not counted.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesOutOfWindow()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync(new DateTime(2026, 5, 18, 9, 0, 0, DateTimeKind.Utc));
        await harness.SeedApplicationAsync(new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc)); // before window

        var from = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 19, 0, 0, 0, DateTimeKind.Utc);
        var paramsJson = BuildParams(from, to);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Header + one day row.
        lines.Should().HaveCount(2);
        lines.Should().Contain("2026-05-18,1");
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
                .UseInMemoryDatabase($"cnas-rpt-new-app-daily-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a ServiceApplication whose <c>CreatedAtUtc</c> matches the supplied instant.</summary>
        public async Task SeedApplicationAsync(DateTime createdUtc)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-NAD",
                    NameRo = "Nad", DescriptionRo = "Nad",
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000055555",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            Db.Applications.Add(new ServiceApplication
            {
                CreatedAtUtc = createdUtc,
                SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value,
                Status = ApplicationStatus.Submitted,
                FormPayloadJson = "{}",
                SubmittedAtUtc = createdUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
