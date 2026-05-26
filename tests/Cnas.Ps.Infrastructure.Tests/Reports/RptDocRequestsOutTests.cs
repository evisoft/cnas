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
/// Integration tests for the Annex 6 report <c>RPT-DOC-REQUESTS-OUT</c> — external
/// document requests that have been sent but not yet resolved. In the absence of a
/// dedicated <c>ExternalDocumentRequest</c> entity, the report sources its rows from
/// <see cref="WorkflowTask"/> rows whose <see cref="WorkflowTask.Title"/> follows the
/// <c>DOC-REQ:&lt;TargetRegistry&gt;</c> convention. <c>SentUtc</c> ≡ <c>CreatedAtUtc</c>
/// and <c>ResolvedUtc</c> ≡ <c>CompletedAtUtc</c>; rows with a non-null
/// <see cref="WorkflowTask.CompletedAtUtc"/> are considered resolved and excluded.
/// </summary>
public class RptDocRequestsOutTests
{
    /// <summary>Fixed UTC clock so "AgeDays" is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOC-REQUESTS-OUT";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedDocRequestAsync("RSP", sentUtc: ClockNow.AddDays(-3), resolvedUtc: null);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be(
            "Request Sqid,Dossier Sqid,Target Registry,Sent (UTC),Age Days");
    }

    /// <summary>Seeds three rows — one in window unresolved, one resolved, one out of window.</summary>
    [Fact]
    public async Task Execute_WithSeededData_ReturnsExpectedRows()
    {
        var harness = Harness.Create();
        await harness.SeedDocRequestAsync("RSP",
            sentUtc: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), resolvedUtc: null);
        await harness.SeedDocRequestAsync("RSUD",
            sentUtc: new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
            resolvedUtc: new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc));
        await harness.SeedDocRequestAsync("SFS",
            sentUtc: new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), resolvedUtc: null);

        var paramsJson = BuildParams(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("RSP", "in-window + unresolved must appear");
        text.Should().NotContain("RSUD", "resolved rows must be filtered out");
        text.Should().NotContain("SFS", "out-of-window rows must be filtered out");
    }

    /// <summary>WorkflowTask rows whose title does NOT follow the DOC-REQ convention must not leak in.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_OnlyDocReqTitles()
    {
        var harness = Harness.Create();
        // Plain workflow task — not a DOC-REQ.
        await harness.SeedRawTaskAsync("Review documents",
            sentUtc: ClockNow.AddDays(-5), resolvedUtc: null);
        // Valid DOC-REQ — must appear.
        await harness.SeedDocRequestAsync("RSP", sentUtc: ClockNow.AddDays(-5), resolvedUtc: null);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("RSP");
        text.Should().NotContain("Review documents");
    }

    /// <summary>Both <c>RequestSqid</c> and <c>DossierSqid</c> are encoded — never raw longs.</summary>
    [Fact]
    public async Task Execute_SqidColumnsAreEncoded()
    {
        var harness = Harness.Create();
        await harness.SeedDocRequestAsync("RSP", sentUtc: ClockNow.AddDays(-2), resolvedUtc: null);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var dataLine = text.Split("\r\n").First(l => l.Contains("RSP"));
        var parts = dataLine.Split(',');
        parts[0].Should().StartWith("sqid-");
        long.TryParse(parts[0], out _).Should().BeFalse();
        parts[1].Should().StartWith("sqid-");
        long.TryParse(parts[1], out _).Should().BeFalse();
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

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-docreq-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a Dossier + a DOC-REQ-titled WorkflowTask referencing it.</summary>
        public async Task SeedDocRequestAsync(string targetRegistry, DateTime sentUtc, DateTime? resolvedUtc)
            => await SeedTaskAsync($"DOC-REQ:{targetRegistry}", sentUtc, resolvedUtc).ConfigureAwait(false);

        /// <summary>Seeds a non-DOC-REQ WorkflowTask used to prove the title filter.</summary>
        public async Task SeedRawTaskAsync(string title, DateTime sentUtc, DateTime? resolvedUtc)
            => await SeedTaskAsync(title, sentUtc, resolvedUtc).ConfigureAwait(false);

        /// <summary>Shared seed for both DOC-REQ and non-DOC-REQ tasks.</summary>
        private async Task SeedTaskAsync(string title, DateTime sentUtc, DateTime? resolvedUtc)
        {
            // We need a Dossier to satisfy the WorkflowTask.DossierId FK.
            var passport = new ServicePassport
            {
                CreatedAtUtc = sentUtc,
                Code = "SP-DOC",
                NameRo = "Doc", DescriptionRo = "Doc",
                FormSchemaJson = "{}", WorkflowCode = "WF",
                MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var solicitant = new Solicitant
            {
                CreatedAtUtc = sentUtc, NationalId = "2000000000999",
                Kind = ApplicantKind.NaturalPerson, DisplayName = "Doc Subject", IsActive = true,
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
                Title = title,
                Status = resolvedUtc is null ? WorkflowTaskStatus.InProgress : WorkflowTaskStatus.Completed,
                CompletedAtUtc = resolvedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }
    }
}
