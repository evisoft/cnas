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
/// Integration tests for the Annex 6c report <c>RPT-DOC-REQUESTS-CLOSED-RECENT</c> —
/// external document requests that have been resolved within the last <c>nDays</c> days.
/// Sourced from <see cref="WorkflowTask"/> rows whose title follows the
/// <c>DOC-REQ:&lt;TargetRegistry&gt;</c> convention and whose
/// <see cref="WorkflowTask.CompletedAtUtc"/> is non-null and ≥ <c>now - nDays</c>.
/// <c>SentUtc</c> ≡ <see cref="AuditableEntity.CreatedAtUtc"/>; <c>ResolvedUtc</c> ≡
/// <see cref="WorkflowTask.CompletedAtUtc"/>; <c>TurnaroundDays</c> is
/// <c>(ResolvedUtc - SentUtc).TotalDays</c> rounded to a single decimal place.
/// </summary>
public class RptDocRequestsClosedRecentTests
{
    /// <summary>Fixed UTC clock so "since now-N" comparisons are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOC-REQUESTS-CLOSED-RECENT";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedDocRequestAsync("RSP",
            sentUtc: ClockNow.AddDays(-3),
            resolvedUtc: ClockNow.AddDays(-1));

        var result = await harness.Service.GenerateAsync(
            Code, """{ "nDays": 30 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be(
            "Request Sqid,Dossier Sqid,Target Registry,Sent (UTC),Resolved (UTC),Turnaround Days");
    }

    /// <summary>
    /// Seeds three rows — one recently resolved (must appear), one resolved long before the
    /// window (must be excluded), and one still open (must also be excluded).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_FiltersByResolvedWindow()
    {
        var harness = Harness.Create();
        await harness.SeedDocRequestAsync("RSP",
            sentUtc: ClockNow.AddDays(-10),
            resolvedUtc: ClockNow.AddDays(-2));
        await harness.SeedDocRequestAsync("OLD",
            sentUtc: ClockNow.AddDays(-100),
            resolvedUtc: ClockNow.AddDays(-95));
        await harness.SeedDocRequestAsync("OPEN",
            sentUtc: ClockNow.AddDays(-1),
            resolvedUtc: null);

        var result = await harness.Service.GenerateAsync(
            Code, """{ "nDays": 30 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("RSP", "recently resolved requests must appear");
        text.Should().NotContain("OLD", "requests resolved before now - nDays must be excluded");
        text.Should().NotContain("OPEN", "still-open requests have no resolution and must be excluded");
    }

    /// <summary>Both Sqid columns are encoded — never raw longs.</summary>
    [Fact]
    public async Task Execute_SqidColumnsAreEncoded()
    {
        var harness = Harness.Create();
        await harness.SeedDocRequestAsync("RSP",
            sentUtc: ClockNow.AddDays(-3),
            resolvedUtc: ClockNow.AddDays(-1));

        var result = await harness.Service.GenerateAsync(
            Code, """{ "nDays": 30 }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var dataLine = text.Split("\r\n").First(l => l.Contains("RSP"));
        var parts = dataLine.Split(',');
        parts[0].Should().StartWith("sqid-");
        long.TryParse(parts[0], out _).Should().BeFalse();
        parts[1].Should().StartWith("sqid-");
        long.TryParse(parts[1], out _).Should().BeFalse();
    }

    /// <summary>Missing or invalid <c>nDays</c> must be rejected.</summary>
    [Fact]
    public async Task Execute_MissingNDays_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>Negative or zero <c>nDays</c> must be rejected — window must be positive.</summary>
    [Fact]
    public async Task Execute_NonPositiveNDays_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, """{ "nDays": 0 }""", ExportFormat.Csv);
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
                .UseInMemoryDatabase($"cnas-rpt-docreq-closed-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a Dossier + DOC-REQ-titled WorkflowTask with the supplied resolution state.</summary>
        public async Task SeedDocRequestAsync(string targetRegistry, DateTime sentUtc, DateTime? resolvedUtc)
        {
            var passport = new ServicePassport
            {
                CreatedAtUtc = sentUtc,
                Code = "SP-DOCC",
                NameRo = "DocC", DescriptionRo = "DocC",
                FormSchemaJson = "{}", WorkflowCode = "WF",
                MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var solicitant = new Solicitant
            {
                CreatedAtUtc = sentUtc, NationalId = "2000000000999",
                Kind = ApplicantKind.NaturalPerson, DisplayName = "Subject", IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = sentUtc, SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id, Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}", SubmittedAtUtc = sentUtc, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = sentUtc, ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16], IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            Db.WorkflowTasks.Add(new WorkflowTask
            {
                CreatedAtUtc = sentUtc,
                DossierId = dossier.Id,
                Title = $"DOC-REQ:{targetRegistry}",
                Status = resolvedUtc is null ? WorkflowTaskStatus.InProgress : WorkflowTaskStatus.Completed,
                CompletedAtUtc = resolvedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
