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
/// Integration tests for the Annex 6f report <c>RPT-CASES-BY-LOCALITY</c> — count of
/// active dossiers grouped by the beneficiary's locality. The locality is derived from
/// <see cref="Solicitant.PostalAddress"/> (the first comma-separated token); when absent
/// the row lands in the <c>UNKNOWN</c> bucket. Rows are ordered by Count desc, then
/// locality (Ordinal) for tie-breaking. No parameters; no Sqid columns (aggregated).
/// </summary>
public class RptCasesByLocalityTests
{
    /// <summary>Fixed UTC clock — every seed row uses it for AuditableEntity timestamps.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-CASES-BY-LOCALITY";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedDossierAsync("Chișinău", dossierClosed: false);

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Locality,Count");
    }

    /// <summary>Three localities — most frequent first. Verifies ordering and counts.</summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsAndOrdersByCountDesc()
    {
        var harness = Harness.Create();
        // Chișinău: 3 dossiers; Bălți: 2; Cahul: 1.
        for (var i = 0; i < 3; i++) await harness.SeedDossierAsync("Chișinău", dossierClosed: false);
        for (var i = 0; i < 2; i++) await harness.SeedDossierAsync("Bălți", dossierClosed: false);
        await harness.SeedDossierAsync("Cahul", dossierClosed: false);

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Skip the header — body rows must appear in count-desc order.
        var body = lines.Skip(1).ToList();
        body[0].Should().Be("Chișinău,3");
        body[1].Should().Be("Bălți,2");
        body[2].Should().Be("Cahul,1");
    }

    /// <summary>Closed dossiers are excluded.</summary>
    [Fact]
    public async Task Execute_ExcludesClosedDossiers()
    {
        var harness = Harness.Create();
        await harness.SeedDossierAsync("Chișinău", dossierClosed: false);
        await harness.SeedDossierAsync("Chișinău", dossierClosed: true);

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("Chișinău,1");
    }

    /// <summary>Beneficiaries with a null/empty <c>PostalAddress</c> land in the UNKNOWN bucket.</summary>
    [Fact]
    public async Task Execute_NullOrEmptyAddress_LandsInUnknownBucket()
    {
        var harness = Harness.Create();
        await harness.SeedDossierAsync(postalAddress: null, dossierClosed: false);
        await harness.SeedDossierAsync(postalAddress: string.Empty, dossierClosed: false);
        await harness.SeedDossierAsync(postalAddress: "   ", dossierClosed: false);

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("UNKNOWN,3");
    }

    /// <summary>Empty dataset still returns success with just the header row.</summary>
    [Fact]
    public async Task Execute_EmptyDataset_ReturnsHeaderOnly()
    {
        var harness = Harness.Create();

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
        lines[0].Should().Be("Locality,Count");
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

        /// <summary>Monotonic counter so beneficiary IDNPs do not collide across seedings.</summary>
        private int _idnpCounter;

        /// <summary>Cached scaffolding ids so seeding doesn't churn rows.</summary>
        private long? _passportId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-locality-{Guid.NewGuid():N}")
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
        /// Seeds an active (or closed) dossier whose owning Solicitant carries the supplied
        /// <paramref name="postalAddress"/>. The report's locality extractor reads the first
        /// comma-separated token so callers may pass just the locality (e.g. <c>"Chișinău"</c>)
        /// or a full address (e.g. <c>"Chișinău, str. Pacii 12"</c>).
        /// </summary>
        public async Task SeedDossierAsync(string? postalAddress, bool dossierClosed)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-LOC",
                    NameRo = "Loc", DescriptionRo = "Loc",
                    FormSchemaJson = "{}", WorkflowCode = "WF",
                    MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
                };
                Db.ServicePassports.Add(passport);
                await Db.SaveChangesAsync();
                _passportId = passport.Id;
            }

            _idnpCounter++;
            var idnp = $"20000001{_idnpCounter:D5}";
            var s = new Solicitant
            {
                CreatedAtUtc = ClockNow, NationalId = idnp,
                Kind = ApplicantKind.NaturalPerson, DisplayName = "Test",
                PostalAddress = postalAddress, IsActive = true,
            };
            Db.Solicitants.Add(s);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow.AddDays(-30), SolicitantId = s.Id,
                ServicePassportId = _passportId.Value, Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}", SubmittedAtUtc = ClockNow.AddDays(-30), IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = ClockNow.AddDays(-30), ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                ClosedAtUtc = dossierClosed ? ClockNow : null,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
