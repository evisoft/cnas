using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Application.UseCases;
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
/// Integration tests for the Annex 6 report <c>RPT-DOC-VERDICT-MIX</c> — distribution of
/// examiner verdicts (Accepted / Rejected / Held) recorded against attached
/// <see cref="Document"/> rows in the <c>[fromUtc, toUtc)</c> window. The verdict integer
/// is interpreted via <see cref="ExaminationVerdict"/>; rows with no verdict or a verdict
/// stamped outside the window are ignored.
/// </summary>
public class RptDocVerdictMixTests
{
    /// <summary>Fixed UTC clock for deterministic generation.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOC-VERDICT-MIX";

    /// <summary>Locks the column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedDocAsync(ExaminationVerdict.Accepted,
            verdictUtc: new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc));

        var paramsJson = BuildParams(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Verdict,Count");
    }

    /// <summary>
    /// Seeds documents with each of the three verdicts inside the window and asserts the
    /// counts match. The report emits one row per verdict (Accepted / Rejected / Held)
    /// regardless of zero counts, so consumers can render a consistent histogram.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_ReturnsExpectedRows()
    {
        var harness = Harness.Create();
        var inWindow = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc);
        await harness.SeedDocAsync(ExaminationVerdict.Accepted, inWindow);
        await harness.SeedDocAsync(ExaminationVerdict.Accepted, inWindow.AddDays(1));
        await harness.SeedDocAsync(ExaminationVerdict.Rejected, inWindow.AddDays(2));
        await harness.SeedDocAsync(ExaminationVerdict.Held, inWindow.AddDays(3));

        var paramsJson = BuildParams(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("Accepted,2");
        lines.Should().Contain("Rejected,1");
        lines.Should().Contain("Held,1");
    }

    /// <summary>
    /// Verdicts stamped outside the [fromUtc, toUtc) half-open window must not contribute,
    /// and documents with no verdict at all must be ignored.
    /// </summary>
    [Fact]
    public async Task Execute_RespectsFilter_OnlyInWindowVerdicts()
    {
        var harness = Harness.Create();
        // In-window — must count.
        await harness.SeedDocAsync(ExaminationVerdict.Accepted,
            new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc));
        // Out of window — must not count.
        await harness.SeedDocAsync(ExaminationVerdict.Rejected,
            new DateTime(2026, 3, 30, 0, 0, 0, DateTimeKind.Utc));
        // At exact upper bound — must not count (half-open).
        await harness.SeedDocAsync(ExaminationVerdict.Held,
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        // No verdict at all — must not count.
        await harness.SeedDocAsync(verdict: null, verdictUtc: null);

        var paramsJson = BuildParams(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("Accepted,1");
        lines.Should().Contain("Rejected,0");
        lines.Should().Contain("Held,0");
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

    /// <summary>Builds the [fromUtc, toUtc) parameters JSON.</summary>
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
                .UseInMemoryDatabase($"cnas-rpt-verdict-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Convenience overload — seeds a Document with the given <paramref name="verdict"/>.</summary>
        public Task SeedDocAsync(ExaminationVerdict verdict, DateTime verdictUtc)
            => SeedDocAsync((int)verdict, verdictUtc);

        /// <summary>Underlying seed — accepts a raw verdict int (or null) for the unverdicted case.</summary>
        public async Task SeedDocAsync(int? verdict, DateTime? verdictUtc)
        {
            // We need a Dossier to satisfy Document.DossierId.
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-DOC",
                NameRo = "Doc", DescriptionRo = "Doc",
                FormSchemaJson = "{}", WorkflowCode = "WF",
                MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow, NationalId = "2000000000999",
                Kind = ApplicantKind.NaturalPerson, DisplayName = "Doc Subject", IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow, SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id, Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}", SubmittedAtUtc = ClockNow, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow, ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16], IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            Db.Documents.Add(new Document
            {
                CreatedAtUtc = verdictUtc ?? ClockNow,
                DossierId = dossier.Id,
                Kind = DocumentKind.Attachment,
                Title = "Attachment",
                MimeType = "application/pdf",
                SizeBytes = 1024,
                StorageObjectKey = $"obj-{Guid.NewGuid():N}",
                StorageBucket = "test",
                ContentSha256Hex = new string('0', 64),
                Verdict = verdict,
                VerdictAtUtc = verdictUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
