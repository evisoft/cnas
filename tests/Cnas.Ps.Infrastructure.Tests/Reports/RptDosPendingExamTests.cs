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
/// Integration tests for the Annex 6 report <c>RPT-DOS-PENDING-EXAM</c> — dossiers in
/// examination longer than <c>nDays</c> days. The "received" timestamp is the dossier's
/// <see cref="Dossier.CreatedAtUtc"/>; "DaysOpen" is computed against the stub clock.
/// </summary>
public class RptDosPendingExamTests
{
    /// <summary>Fixed UTC clock so that "DaysOpen" is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOS-PENDING-EXAM";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedInExamAsync("2000000000001", "X-100", receivedUtc: ClockNow.AddDays(-15), examinerId: 7);

        var result = await harness.Service.GenerateAsync(Code, """{ "nDays": 5 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be(
            "Dossier Sqid,Beneficiary IDNP,Assigned Examiner,Received (UTC),Days Open");
    }

    /// <summary>Seeds three rows: one over-threshold, one under-threshold, one closed.</summary>
    [Fact]
    public async Task Execute_WithSeededData_ReturnsExpectedRows()
    {
        var harness = Harness.Create();
        // 30-day-old, still in examination — must appear (IDNP "2000000000011").
        await harness.SeedInExamAsync(
            "2000000000011", "Over Threshold", receivedUtc: ClockNow.AddDays(-30), examinerId: 11);
        // 2-day-old, still in examination — must NOT appear when nDays = 7 (IDNP "2000000000022").
        await harness.SeedInExamAsync(
            "2000000000022", "Under Threshold", receivedUtc: ClockNow.AddDays(-2), examinerId: 11);
        // Already closed — must NOT appear regardless of age (IDNP "2000000000033").
        await harness.SeedClosedAsync(
            "2000000000033", "Already Closed", receivedUtc: ClockNow.AddDays(-30), examinerId: 11);

        var result = await harness.Service.GenerateAsync(Code, """{ "nDays": 7 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        // The report exposes IDNP rather than full name, so use the seeded IDNPs as discriminators.
        text.Should().Contain("2000000000011");
        text.Should().NotContain("2000000000022");
        text.Should().NotContain("2000000000033");
    }

    /// <summary>A row at the boundary (exactly nDays old) is filtered out — the inequality is strict.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_StrictGreaterThan()
    {
        var harness = Harness.Create();
        await harness.SeedInExamAsync(
            "2000000000004", "Exactly N", receivedUtc: ClockNow.AddDays(-10), examinerId: 5);

        var result = await harness.Service.GenerateAsync(Code, """{ "nDays": 10 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().NotContain("Exactly N",
            "Dossiers exactly N days old must NOT appear; the filter is strict > N.");
    }

    /// <summary><c>DossierSqid</c> is always Sqid-encoded.</summary>
    [Fact]
    public async Task Execute_DossierSqidIsEncoded()
    {
        var harness = Harness.Create();
        await harness.SeedInExamAsync(
            "2000000000005", "Sqid Subj", receivedUtc: ClockNow.AddDays(-50), examinerId: 1);

        var result = await harness.Service.GenerateAsync(Code, """{ "nDays": 5 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var line = text.Split("\r\n").First(l => l.Contains("2000000000005"));
        var sqid = line.Split(',')[0];
        sqid.Should().StartWith("sqid-");
        long.TryParse(sqid, out _).Should().BeFalse();
    }

    /// <summary>Missing/non-positive <c>nDays</c> must yield a validation failure.</summary>
    [Fact]
    public async Task Execute_MissingNDays_ReturnsValidationFailed()
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
        /// <summary>InMemory context for the test.</summary>
        public required CnasDbContext Db { get; init; }

        /// <summary>The system under test.</summary>
        public required ReportingService Service { get; init; }

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-pending-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds an in-examination dossier (UnderExamination, ClosedAtUtc null).</summary>
        public Task SeedInExamAsync(string idnp, string displayName, DateTime receivedUtc, long examinerId)
            => SeedDossierAsync(idnp, displayName, receivedUtc, examinerId,
                ApplicationStatus.UnderExamination, closedAtUtc: null);

        /// <summary>Seeds a closed dossier (Closed status, ClosedAtUtc set) — must be filtered out.</summary>
        public Task SeedClosedAsync(string idnp, string displayName, DateTime receivedUtc, long examinerId)
            => SeedDossierAsync(idnp, displayName, receivedUtc, examinerId,
                ApplicationStatus.Closed, closedAtUtc: receivedUtc.AddDays(2));

        /// <summary>Shared seed helper used by both Open and Closed variants above.</summary>
        private async Task SeedDossierAsync(
            string idnp,
            string displayName,
            DateTime receivedUtc,
            long examinerId,
            ApplicationStatus status,
            DateTime? closedAtUtc)
        {
            var passport = new ServicePassport
            {
                CreatedAtUtc = receivedUtc,
                Code = "SP-EXAM",
                NameRo = "Exam",
                DescriptionRo = "Exam",
                FormSchemaJson = "{}",
                WorkflowCode = "WF",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var solicitant = new Solicitant
            {
                CreatedAtUtc = receivedUtc,
                NationalId = idnp,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = displayName,
                IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var application = new ServiceApplication
            {
                CreatedAtUtc = receivedUtc,
                SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id,
                Status = status,
                FormPayloadJson = "{}",
                SubmittedAtUtc = receivedUtc,
                IsActive = true,
            };
            Db.Applications.Add(application);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = receivedUtc,
                ApplicationId = application.Id,
                DossierNumber = $"D-{idnp[^4..]}",
                AssignedExaminerId = examinerId,
                ClosedAtUtc = closedAtUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture; // suppress unused-using
        }
    }
}
