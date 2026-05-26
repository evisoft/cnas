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
/// Integration tests for the Annex 6g report <c>RPT-BENEFICIARIES-BY-SERVICE-TYPE</c> — the
/// distinct beneficiary count per <see cref="ServicePassport.Code"/> across active dossiers
/// as of <c>asOfUtc</c>. The "active" predicate mirrors <c>RPT-PEN-ACTIVE</c>: Approved
/// application + Dossier.AcceptedAtUtc &lt;= asOfUtc + (ClosedAtUtc is null OR ClosedAtUtc &gt; asOfUtc).
/// </summary>
public class RptBeneficiariesByServiceTypeTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-BENEFICIARIES-BY-SERVICE-TYPE";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Service Code,Service Title (RO),Unique Beneficiary Count");
    }

    /// <summary>
    /// Seeds dossiers across two service passports with mixed solicitants — including one
    /// solicitant on the same passport twice (must be counted once). Verifies the per-service
    /// distinct count and exclusion of closed / non-approved dossiers.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsByServiceWithDistinctBeneficiaries()
    {
        var harness = Harness.Create();
        var passA = await harness.SeedServicePassportAsync("SP-A", "Service A");
        var passB = await harness.SeedServicePassportAsync("SP-B", "Service B");

        var solicitant1 = await harness.SeedSolicitantAsync("2000000000001");
        var solicitant2 = await harness.SeedSolicitantAsync("2000000000002");
        var solicitant3 = await harness.SeedSolicitantAsync("2000000000003");

        // Two active approved dossiers on SP-A for the same solicitant — counted once.
        await harness.SeedDossierAsync(passA, solicitant1, ApplicationStatus.Approved,
            acceptedUtc: ClockNow.AddDays(-30), closedUtc: null);
        await harness.SeedDossierAsync(passA, solicitant1, ApplicationStatus.Approved,
            acceptedUtc: ClockNow.AddDays(-20), closedUtc: null);
        // Another solicitant on SP-A.
        await harness.SeedDossierAsync(passA, solicitant2, ApplicationStatus.Approved,
            acceptedUtc: ClockNow.AddDays(-10), closedUtc: null);
        // SP-B with one solicitant.
        await harness.SeedDossierAsync(passB, solicitant3, ApplicationStatus.Approved,
            acceptedUtc: ClockNow.AddDays(-15), closedUtc: null);
        // Closed before asOfUtc — must be excluded.
        await harness.SeedDossierAsync(passB, solicitant1, ApplicationStatus.Approved,
            acceptedUtc: ClockNow.AddDays(-60), closedUtc: ClockNow.AddDays(-1));
        // Non-approved — must be excluded.
        await harness.SeedDossierAsync(passA, solicitant3, ApplicationStatus.Rejected,
            acceptedUtc: ClockNow.AddDays(-5), closedUtc: null);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // SP-A: solicitants 1+2 distinct -> 2. SP-B: solicitant 3 -> 1.
        lines.Should().Contain("SP-A,Service A,2");
        lines.Should().Contain("SP-B,Service B,1");
    }

    /// <summary>Passports with zero active beneficiaries do not produce a row.</summary>
    [Fact]
    public async Task Execute_PassportWithoutActiveDossiers_EmitsNoRow()
    {
        var harness = Harness.Create();
        await harness.SeedServicePassportAsync("SP-EMPTY", "Empty Service");

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1); // header only
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

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-benbyservice-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a <see cref="ServicePassport"/> and returns its id.</summary>
        public async Task<long> SeedServicePassportAsync(string code, string nameRo)
        {
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = code,
                NameRo = nameRo, DescriptionRo = nameRo,
                FormSchemaJson = "{}", WorkflowCode = "WF",
                MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();
            return passport.Id;
        }

        /// <summary>Seeds a <see cref="Solicitant"/> and returns its id.</summary>
        public async Task<long> SeedSolicitantAsync(string nationalId)
        {
            var s = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = nationalId,
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = $"Person-{nationalId}",
                IsActive = true,
            };
            Db.Solicitants.Add(s);
            await Db.SaveChangesAsync();
            return s.Id;
        }

        /// <summary>
        /// Seeds an Application/Dossier pair under the supplied passport for the supplied
        /// solicitant with the given lifecycle dates and application status.
        /// </summary>
        public async Task SeedDossierAsync(
            long passportId, long solicitantId,
            ApplicationStatus status, DateTime? acceptedUtc, DateTime? closedUtc)
        {
            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow.AddDays(-60),
                SolicitantId = solicitantId,
                ServicePassportId = passportId,
                Status = status,
                FormPayloadJson = "{}",
                SubmittedAtUtc = ClockNow.AddDays(-60),
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = ClockNow.AddDays(-60),
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
