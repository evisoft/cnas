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
/// Integration tests for the Annex 6e report <c>RPT-DOCUMENT-AGE-DISTRIBUTION</c> — the
/// distribution of document ages (in days) inside currently-active dossiers as of a UTC
/// moment. The histogram is dense over the five fixed buckets <c>&lt;7d</c>, <c>7-30d</c>,
/// <c>30-90d</c>, <c>90-180d</c>, <c>&gt;180d</c>. Source: <see cref="Document"/> rows
/// attached (<see cref="Document.DossierId"/> non-null) to dossiers whose
/// <see cref="Dossier.ClosedAtUtc"/> is null.
/// </summary>
public class RptDocumentAgeDistributionTests
{
    /// <summary>Fixed UTC clock so bucket boundaries are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOCUMENT-AGE-DISTRIBUTION";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedDocumentAsync(createdUtc: ClockNow.AddDays(-3), dossierClosed: false);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Age Bucket,Count");
    }

    /// <summary>
    /// Seeds one document in each bucket plus one attached to a closed dossier (excluded).
    /// Verifies the five buckets are emitted with the expected counts.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_BucketsByAge()
    {
        var harness = Harness.Create();
        await harness.SeedDocumentAsync(ClockNow.AddDays(-3), dossierClosed: false);     // <7d
        await harness.SeedDocumentAsync(ClockNow.AddDays(-15), dossierClosed: false);    // 7-30d
        await harness.SeedDocumentAsync(ClockNow.AddDays(-60), dossierClosed: false);    // 30-90d
        await harness.SeedDocumentAsync(ClockNow.AddDays(-120), dossierClosed: false);   // 90-180d
        await harness.SeedDocumentAsync(ClockNow.AddDays(-365), dossierClosed: false);   // >180d
        // Attached to a closed dossier — must be excluded.
        await harness.SeedDocumentAsync(ClockNow.AddDays(-3), dossierClosed: true);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("<7d,1");
        lines.Should().Contain("7-30d,1");
        lines.Should().Contain("30-90d,1");
        lines.Should().Contain("90-180d,1");
        lines.Should().Contain(">180d,1");
    }

    /// <summary>All five buckets are always emitted, even with zero documents.</summary>
    [Fact]
    public async Task Execute_DenseHistogram_EmitsAllFiveBucketsEvenWhenEmpty()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("<7d,0");
        lines.Should().Contain("7-30d,0");
        lines.Should().Contain("30-90d,0");
        lines.Should().Contain("90-180d,0");
        lines.Should().Contain(">180d,0");
    }

    /// <summary>Documents on closed dossiers must be filtered out.</summary>
    [Fact]
    public async Task Execute_ExcludesClosedDossiers()
    {
        var harness = Harness.Create();
        await harness.SeedDocumentAsync(ClockNow.AddDays(-3), dossierClosed: false);
        await harness.SeedDocumentAsync(ClockNow.AddDays(-3), dossierClosed: true);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Only the active-dossier document survives.
        lines.Should().Contain("<7d,1");
    }

    /// <summary>Missing <c>asOfUtc</c> must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingAsOfUtc_ReturnsValidationFailed()
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

        /// <summary>Cached scaffolding ids so seeding doesn't churn rows.</summary>
        private long? _passportId;
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-docage-{Guid.NewGuid():N}")
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
        /// Seeds a <see cref="Document"/> attached to a Dossier. When
        /// <paramref name="dossierClosed"/> is true the dossier carries a
        /// <see cref="Dossier.ClosedAtUtc"/> and the document must be excluded by the report.
        /// </summary>
        public async Task SeedDocumentAsync(DateTime createdUtc, bool dossierClosed)
        {
            if (_passportId is null)
            {
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
                CreatedAtUtc = createdUtc, SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value, Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}", SubmittedAtUtc = createdUtc, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = createdUtc, ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                ClosedAtUtc = dossierClosed ? ClockNow : null,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            Db.Documents.Add(new Document
            {
                CreatedAtUtc = createdUtc,
                DossierId = dossier.Id,
                Kind = DocumentKind.Attachment,
                Title = $"Doc-{Guid.NewGuid():N}"[..12],
                MimeType = "application/pdf",
                SizeBytes = 1024,
                StorageObjectKey = $"obj-{Guid.NewGuid():N}",
                StorageBucket = "test",
                ContentSha256Hex = new string('a', 64),
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
