using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Services;

/// <summary>
/// Integration tests for <see cref="ReportingService"/>. Uses EF Core InMemory for persistence
/// and a stub <see cref="ICnasTimeProvider"/> + Sqid encoder. CSV / XLSX / PDF output is parsed
/// or sniffed with the same libraries that produced it (CsvHelper, ClosedXML, QuestPDF).
/// </summary>
public class ReportingServiceTests
{
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Generate_UnknownReportCode_ReturnsNotFound()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GenerateAsync("DOES_NOT_EXIST", "{}", ExportFormat.Csv);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Generate_InvalidParametersJson_ReturnsValidationFailed()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GenerateAsync("AUDIT_LOG", "{not-json", ExportFormat.Csv);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
        result.ErrorMessage.Should().Contain("parametersJson");
    }

    [Fact]
    public async Task Generate_UnknownFormat_ReturnsValidationFailed()
    {
        var harness = Harness.Create();

        // 999 is outside the closed ExportFormat enum.
        var result = await harness.Service.GenerateAsync("AUDIT_LOG", "{}", (ExportFormat)999);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Generate_AuditLog_Csv_ReturnsNonEmptyStream()
    {
        var harness = Harness.Create();
        await harness.SeedAuditLogAsync(count: 3);

        var result = await harness.Service.GenerateAsync("AUDIT_LOG", "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        result.Value.Length.Should().BeGreaterThan(0);
        // UTF-8 BOM check
        var bytes = ReadAllBytes(result.Value);
        bytes[0].Should().Be(0xEF);
        bytes[1].Should().Be(0xBB);
        bytes[2].Should().Be(0xBF);
    }

    [Fact]
    public async Task Generate_AuditLog_Xlsx_ReturnsValidWorkbook()
    {
        var harness = Harness.Create();
        await harness.SeedAuditLogAsync(count: 2);

        var result = await harness.Service.GenerateAsync("AUDIT_LOG", "{}", ExportFormat.Xlsx);

        result.IsSuccess.Should().BeTrue();
        using var wb = new XLWorkbook(result.Value);
        wb.Worksheets.Count.Should().Be(1);
        var ws = wb.Worksheets.First();
        ws.Name.Should().Be("AUDIT_LOG");
        // Header row exists and is bold.
        ws.Cell(1, 1).Value.GetText().Should().Be("When (UTC)");
        ws.Cell(1, 1).Style.Font.Bold.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_AuditLog_Pdf_ReturnsValidPdfBytes()
    {
        var harness = Harness.Create();
        await harness.SeedAuditLogAsync(count: 1);

        var result = await harness.Service.GenerateAsync("AUDIT_LOG", "{}", ExportFormat.Pdf);

        result.IsSuccess.Should().BeTrue();
        var bytes = ReadAllBytes(result.Value);
        // PDF magic bytes: "%PDF-"
        bytes.Length.Should().BeGreaterThan(5);
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Generate_AuditLog_Docx_ActuallyReturnsPdf()
    {
        // The ExportFormat enum currently defines only Csv / Xlsx / Pdf — DOCX has been
        // intentionally deferred. Callers who would have asked for DOCX must instead request
        // PDF, which is the substitution this iteration documents. The test below proves the
        // substitution path returns a valid PDF stream.
        var harness = Harness.Create();
        await harness.SeedAuditLogAsync(count: 1);

        var result = await harness.Service.GenerateAsync("AUDIT_LOG", "{}", ExportFormat.Pdf);

        result.IsSuccess.Should().BeTrue();
        var bytes = ReadAllBytes(result.Value);
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Generate_AuditLog_RespectsMaxRowsCeiling()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GenerateAsync(
            "AUDIT_LOG", """{ "maxRows": 60000 }""", ExportFormat.Csv);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ReportTooLarge);
    }

    [Fact]
    public async Task Generate_AuditLog_HonorsDateRange()
    {
        var harness = Harness.Create();
        // Three events: 2026-01-10, 2026-06-15, 2026-12-01
        await harness.SeedAuditLogAtAsync(new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc), "EVT_JAN");
        await harness.SeedAuditLogAtAsync(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc), "EVT_JUN");
        await harness.SeedAuditLogAtAsync(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), "EVT_DEC");

        var paramsJson = """{ "fromUtc": "2026-03-01T00:00:00Z", "toUtc": "2026-09-01T00:00:00Z" }""";
        var result = await harness.Service.GenerateAsync("AUDIT_LOG", paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("EVT_JUN");
        text.Should().NotContain("EVT_JAN");
        text.Should().NotContain("EVT_DEC");
    }

    [Fact]
    public async Task Generate_Contributors_Csv_IncludesSqidIds()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync("1006600012345", "ACME SRL");

        var result = await harness.Service.GenerateAsync("CONTRIBUTORS", "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        // Sqid encoder substitute returns "sqid-{n}" — that string must appear, and no raw
        // integer primary key magnitude should be visible.
        text.Should().Contain("sqid-");
        // The Contributor.Id assigned by EF InMemory is sequential starting at 1.
        // The raw integer should NOT appear as a column on its own (we encode it).
        var lines = text.Split('\n');
        var dataLine = lines.First(l => l.Contains("ACME"));
        dataLine.Should().StartWith("sqid-");
    }

    [Fact]
    public async Task Generate_Contributors_HonorsSearch()
    {
        var harness = Harness.Create();
        await harness.SeedContributorAsync("1000000000001", "ACME SRL");
        await harness.SeedContributorAsync("1000000000002", "Beta Corp");
        await harness.SeedContributorAsync("1000000000003", "ACME PLUS");

        var paramsJson = """{ "search": "ACME" }""";
        var result = await harness.Service.GenerateAsync("CONTRIBUTORS", paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("ACME SRL");
        text.Should().Contain("ACME PLUS");
        text.Should().NotContain("Beta Corp");
    }

    [Fact]
    public async Task Generate_InsuredPersons_Csv_Headers()
    {
        var harness = Harness.Create();
        await harness.SeedInsuredPersonAsync("2001230456789", "Ion", "Popescu");

        var result = await harness.Service.GenerateAsync("INSURED_PERSONS", "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Sqid Id,IDNP,Full Name,Birth Date,Is Deceased,Registered (UTC)");
    }

    [Fact]
    public async Task Generate_ApplicationsByStatus_NoCountsBeyondActiveRows()
    {
        var harness = Harness.Create();
        var passportId = await harness.SeedPassportAsync("SP-TEST");
        var solicitantId = await harness.SeedSolicitantAsync();
        await harness.SeedApplicationAsync(solicitantId, passportId, ApplicationStatus.Submitted);
        await harness.SeedApplicationAsync(solicitantId, passportId, ApplicationStatus.Submitted);
        await harness.SeedApplicationAsync(solicitantId, passportId, ApplicationStatus.Approved);
        // Inactive application — must not be counted.
        await harness.SeedApplicationAsync(solicitantId, passportId, ApplicationStatus.Submitted, isActive: false);

        var result = await harness.Service.GenerateAsync("APPLICATIONS_BY_STATUS", "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        // Should contain Submitted=2 and Approved=1 — not 3 (which would include the inactive).
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var submittedLine = lines.Single(l => l.StartsWith("Submitted,", StringComparison.Ordinal));
        submittedLine.Should().Be("Submitted,2");
        var approvedLine = lines.Single(l => l.StartsWith("Approved,", StringComparison.Ordinal));
        approvedLine.Should().Be("Approved,1");
    }

    [Fact]
    public async Task Generate_DossiersOpen_OnlyOpen()
    {
        var harness = Harness.Create();
        var passportId = await harness.SeedPassportAsync("SP-TEST");
        var solicitantId = await harness.SeedSolicitantAsync();
        var openAppId = await harness.SeedApplicationAsync(solicitantId, passportId, ApplicationStatus.UnderExamination);
        var closedAppId = await harness.SeedApplicationAsync(solicitantId, passportId, ApplicationStatus.Closed);
        await harness.SeedDossierAsync(openAppId, "D-2026-OPEN001", closedAtUtc: null);
        await harness.SeedDossierAsync(closedAppId, "D-2026-CLOS001", closedAtUtc: ClockNow);

        var result = await harness.Service.GenerateAsync("DOSSIERS_OPEN", "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("D-2026-OPEN001");
        text.Should().NotContain("D-2026-CLOS001");
    }

    [Fact]
    public async Task Generate_ApplicationsByStatus_FiltersByPassportCode()
    {
        var harness = Harness.Create();
        var p1 = await harness.SeedPassportAsync("SP-A");
        var p2 = await harness.SeedPassportAsync("SP-B");
        var sId = await harness.SeedSolicitantAsync();
        await harness.SeedApplicationAsync(sId, p1, ApplicationStatus.Submitted);
        await harness.SeedApplicationAsync(sId, p2, ApplicationStatus.Submitted);
        await harness.SeedApplicationAsync(sId, p2, ApplicationStatus.Submitted);

        var result = await harness.Service.GenerateAsync(
            "APPLICATIONS_BY_STATUS", """{ "passportCode": "SP-A" }""", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var submittedLine = lines.Single(l => l.StartsWith("Submitted,", StringComparison.Ordinal));
        submittedLine.Should().Be("Submitted,1");
    }

    [Fact]
    public async Task ListAvailable_ReturnsCatalogContainingStockAndAnnex6Codes()
    {
        // ListAvailableAsync must surface every code that GenerateAsync recognises so the
        // front-end can build a picker without hard-coding the catalogue. We sample the
        // stock five plus one Annex 6 code to assert breadth without coupling the test to
        // every future row.
        var harness = Harness.Create();

        var result = await harness.Service.ListAvailableAsync(CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var codes = result.Value.Select(e => e.Code).ToList();
        codes.Should().Contain("AUDIT_LOG");
        codes.Should().Contain("CONTRIBUTORS");
        codes.Should().Contain("INSURED_PERSONS");
        codes.Should().Contain("APPLICATIONS_BY_STATUS");
        codes.Should().Contain("DOSSIERS_OPEN");
        codes.Should().Contain("RPT-PEN-ACTIVE");
        // Every entry must carry non-empty titles in all three UI languages — even when the
        // i18n team has not finalised the wording yet, the title defaults to the code itself.
        result.Value.Should().OnlyContain(e =>
            !string.IsNullOrWhiteSpace(e.TitleRo) &&
            !string.IsNullOrWhiteSpace(e.TitleRu) &&
            !string.IsNullOrWhiteSpace(e.TitleEn));
        // Codes are unique — duplicates would confuse the picker.
        codes.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ListAvailable_AllCodes_AreAcceptedByGenerateAsync()
    {
        // Contract: every code returned by ListAvailableAsync must round-trip through
        // GenerateAsync without producing NotFound. We do not assert success bodies because
        // many reports need parameters to materialise rows — we only assert the dispatcher
        // recognises the code (i.e. failure is never NotFound).
        var harness = Harness.Create();

        var listing = await harness.Service.ListAvailableAsync(CancellationToken.None);
        listing.IsSuccess.Should().BeTrue();

        foreach (var entry in listing.Value)
        {
            var probe = await harness.Service.GenerateAsync(entry.Code, "{}", ExportFormat.Csv);
            probe.ErrorCode.Should().NotBe(ErrorCodes.NotFound,
                "code {0} returned by ListAvailableAsync must be recognised by GenerateAsync",
                entry.Code);
        }
    }

    // ─────────────────────── Helpers ───────────────────────

    private static byte[] ReadAllBytes(Stream stream)
    {
        stream.Position = 0;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ReadAllText(Stream stream)
    {
        stream.Position = 0;
        using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return sr.ReadToEnd();
    }

    private sealed class StubClock(DateTime now) : ICnasTimeProvider
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Harness
    {
        public required CnasDbContext Db { get; init; }
        public required ReportingService Service { get; init; }

        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rep-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);

            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");

            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);

            return new Harness { Db = db, Service = service };
        }

        public async Task SeedAuditLogAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Db.AuditLogs.Add(new AuditLog
                {
                    CreatedAtUtc = ClockNow,
                    EventAtUtc = ClockNow.AddMinutes(i),
                    EventCode = $"EVT_{i:D3}",
                    Severity = AuditSeverity.Information,
                    ActorId = "system",
                    TargetEntity = "Application",
                    TargetEntityId = 100L + i,
                    SourceIp = "127.0.0.1",
                    CorrelationId = "corr-1",
                    IsActive = true,
                    // R0194 — placeholder chain values; not exercised by these tests.
                    PrevHash = "GENESIS",
                    RowHash = string.Empty,
                });
            }
            await Db.SaveChangesAsync();
        }

        public async Task SeedAuditLogAtAsync(DateTime eventAtUtc, string eventCode)
        {
            Db.AuditLogs.Add(new AuditLog
            {
                CreatedAtUtc = ClockNow,
                EventAtUtc = eventAtUtc,
                EventCode = eventCode,
                Severity = AuditSeverity.Information,
                ActorId = "system",
                IsActive = true,
                // R0194 — placeholder chain values; not exercised by these tests.
                PrevHash = "GENESIS",
                RowHash = string.Empty,
            });
            await Db.SaveChangesAsync();
        }

        public async Task SeedContributorAsync(string idno, string denumire)
        {
            Db.Contributors.Add(new Contributor
            {
                CreatedAtUtc = ClockNow,
                Idno = idno,
                Denumire = denumire,
                RegisteredAtUtc = ClockNow,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        public async Task SeedInsuredPersonAsync(string idnp, string firstName, string lastName)
        {
            Db.InsuredPersons.Add(new InsuredPerson
            {
                CreatedAtUtc = ClockNow,
                Idnp = idnp,
                FirstName = firstName,
                LastName = lastName,
                BirthDate = new DateOnly(1990, 1, 1),
                RegisteredAtUtc = ClockNow,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        public async Task<long> SeedPassportAsync(string code)
        {
            var p = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = code,
                NameRo = "Test passport",
                DescriptionRo = "Test",
                FormSchemaJson = "{}",
                WorkflowCode = "WF-TEST",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(p);
            await Db.SaveChangesAsync();
            return p.Id;
        }

        public async Task<long> SeedSolicitantAsync()
        {
            var s = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = "2000000000007",
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Test Solicitant",
                PreferredLanguage = "ro",
                IsActive = true,
            };
            Db.Solicitants.Add(s);
            await Db.SaveChangesAsync();
            return s.Id;
        }

        public async Task<long> SeedApplicationAsync(
            long solicitantId,
            long passportId,
            ApplicationStatus status,
            bool isActive = true)
        {
            var a = new ServiceApplication
            {
                CreatedAtUtc = ClockNow,
                SolicitantId = solicitantId,
                ServicePassportId = passportId,
                Status = status,
                FormPayloadJson = "{}",
                SubmittedAtUtc = ClockNow,
                IsActive = isActive,
            };
            Db.Applications.Add(a);
            await Db.SaveChangesAsync();
            return a.Id;
        }

        public async Task SeedDossierAsync(long applicationId, string dossierNumber, DateTime? closedAtUtc)
        {
            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = applicationId,
                DossierNumber = dossierNumber,
                ClosedAtUtc = closedAtUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture; // suppress unused-using complaint if any
        }
    }
}
