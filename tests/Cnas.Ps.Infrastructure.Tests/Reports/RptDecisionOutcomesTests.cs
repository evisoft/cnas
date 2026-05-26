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
/// Integration tests for the Annex 6 report <c>RPT-DECISION-OUTCOMES</c> — distribution of
/// decision outcomes (Approved/Rejected) by service code, scoped to a single month
/// (first-of-month UTC anchor). Aggregated; no Sqid columns appear in the row payload.
/// </summary>
public class RptDecisionOutcomesTests
{
    /// <summary>Fixed UTC clock.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DECISION-OUTCOMES";

    /// <summary>Locks the column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync("SP-A", ApplicationStatus.Approved,
            closedAtUtc: new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc));

        var paramsJson = """{ "monthUtc": "2026-05-15T00:00:00Z" }""";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Service Code,Outcome,Count");
    }

    /// <summary>Seeds multiple decisions across two services and verifies the grouped counts.</summary>
    [Fact]
    public async Task Execute_WithSeededData_ReturnsExpectedRows()
    {
        var harness = Harness.Create();
        var may = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        await harness.SeedApplicationAsync("SP-A", ApplicationStatus.Approved, closedAtUtc: may);
        await harness.SeedApplicationAsync("SP-A", ApplicationStatus.Approved, closedAtUtc: may.AddDays(1));
        await harness.SeedApplicationAsync("SP-A", ApplicationStatus.Rejected, closedAtUtc: may.AddDays(2));
        await harness.SeedApplicationAsync("SP-B", ApplicationStatus.Approved, closedAtUtc: may.AddDays(3));

        var paramsJson = """{ "monthUtc": "2026-05-01T00:00:00Z" }""";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("SP-A,Approved,2");
        lines.Should().Contain("SP-A,Rejected,1");
        lines.Should().Contain("SP-B,Approved,1");
    }

    /// <summary>Decisions outside the target month must not appear (passing a mid-month value still anchors to month 1st).</summary>
    [Fact]
    public async Task Execute_RespectsFilter_OnlyInMonth()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync("SP-IN", ApplicationStatus.Approved,
            closedAtUtc: new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc));
        await harness.SeedApplicationAsync("SP-OUT", ApplicationStatus.Approved,
            closedAtUtc: new DateTime(2026, 4, 30, 23, 59, 59, DateTimeKind.Utc));
        await harness.SeedApplicationAsync("SP-OUT2", ApplicationStatus.Approved,
            closedAtUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        // Provide a mid-month value to prove the impl anchors to the 1st of that month.
        var paramsJson = """{ "monthUtc": "2026-05-23T12:34:56Z" }""";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("SP-IN,Approved,1");
        text.Should().NotContain("SP-OUT,");
        text.Should().NotContain("SP-OUT2,");
    }

    /// <summary>Submitted/Draft applications without final Approved/Rejected status are ignored.</summary>
    [Fact]
    public async Task Execute_IgnoresNonDecisionStatuses()
    {
        var harness = Harness.Create();
        var may = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        await harness.SeedApplicationAsync("SP-X", ApplicationStatus.Submitted, closedAtUtc: may);
        await harness.SeedApplicationAsync("SP-X", ApplicationStatus.UnderExamination, closedAtUtc: may);
        await harness.SeedApplicationAsync("SP-X", ApplicationStatus.Approved, closedAtUtc: may);

        var paramsJson = """{ "monthUtc": "2026-05-01T00:00:00Z" }""";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("SP-X,Approved,1");
        // Only one decision outcome row for SP-X — Submitted / UnderExamination are not decisions.
        lines.Count(l => l.StartsWith("SP-X,", StringComparison.Ordinal)).Should().Be(1);
    }

    /// <summary>Missing monthUtc must be rejected.</summary>
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

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-outcomes-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Caches passport ids by code so repeated calls for the same code reuse the row.</summary>
        private readonly Dictionary<string, long> _passportIds = new(StringComparer.Ordinal);

        /// <summary>Caches a single Solicitant id so we don't churn IDNPs.</summary>
        private long? _solicitantId;

        /// <summary>
        /// Seeds a ServiceApplication with the given <paramref name="status"/> and
        /// <paramref name="closedAtUtc"/> linked to a passport carrying <paramref name="passportCode"/>.
        /// </summary>
        public async Task SeedApplicationAsync(string passportCode, ApplicationStatus status, DateTime closedAtUtc)
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

            if (_solicitantId is null)
            {
                var s = new Solicitant
                {
                    CreatedAtUtc = ClockNow,
                    NationalId = "2000000099999",
                    Kind = ApplicantKind.NaturalPerson,
                    DisplayName = "Test",
                    IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            Db.Applications.Add(new ServiceApplication
            {
                CreatedAtUtc = closedAtUtc,
                SolicitantId = _solicitantId.Value,
                ServicePassportId = passportId,
                Status = status,
                FormPayloadJson = "{}",
                SubmittedAtUtc = closedAtUtc.AddDays(-1),
                ClosedAtUtc = closedAtUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture; // suppress unused-using complaint
        }
    }
}
