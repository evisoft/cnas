using System.Globalization;
using System.Text;
using Cnas.Ps.Application.Abstractions;
using Cnas.Ps.Contracts;
using Cnas.Ps.Core.Common;
using Cnas.Ps.Core.Domain;
using Cnas.Ps.Infrastructure.Persistence;
using Cnas.Ps.Infrastructure.Services;
using Cnas.Ps.Infrastructure.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cnas.Ps.Infrastructure.Tests.Reports;

/// <summary>
/// Integration tests for the Annex 6j report <c>RPT-DOSSIERS-CLOSED-BY-OUTCOME</c> — count of
/// <see cref="Dossier"/> rows whose <see cref="Dossier.ClosedAtUtc"/> falls in the half-open UTC
/// window <c>[fromUtc, toUtc)</c>, bucketed by the closure outcome derived from the joined
/// <see cref="ServiceApplication.Status"/>. The three outcome rows (<c>Approved</c> /
/// <c>Rejected</c> / <c>Cancelled</c>) are emitted densely so consumers can rely on a stable shape.
/// </summary>
public class RptDossiersClosedByOutcomeTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOSSIERS-CLOSED-BY-OUTCOME";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Outcome,Count");
    }

    /// <summary>
    /// Seeds two Approved dossiers, one Rejected, and one Withdrawn (mapped to Cancelled) all
    /// closed in window, plus one Approved closed out of window (excluded) and one soft-deleted
    /// Rejected in window (excluded). Verifies the dense three-row outcome contract and that the
    /// rows are emitted in the canonical order Approved / Rejected / Cancelled.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_EmitsDenseThreeRowOutcomeHistogram()
    {
        var harness = Harness.Create();
        await harness.SeedClosedDossierAsync(ApplicationStatus.Approved, ClockNow.AddDays(-1));
        await harness.SeedClosedDossierAsync(ApplicationStatus.Approved, ClockNow.AddDays(-2));
        await harness.SeedClosedDossierAsync(ApplicationStatus.Rejected, ClockNow.AddDays(-3));
        await harness.SeedClosedDossierAsync(ApplicationStatus.Withdrawn, ClockNow.AddDays(-4));
        // Out-of-window — excluded.
        await harness.SeedClosedDossierAsync(ApplicationStatus.Approved, ClockNow.AddDays(-100));
        // Soft-deleted in-window — excluded.
        await harness.SeedClosedDossierAsync(ApplicationStatus.Rejected, ClockNow.AddDays(-5), isActive: false);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 dense outcome rows.
        lines.Should().HaveCount(4);
        lines[1].Should().Be("Approved,2");
        lines[2].Should().Be("Rejected,1");
        lines[3].Should().Be("Cancelled,1");
    }

    /// <summary>
    /// An empty window still emits the dense three-row outcome histogram (zero counts) — the
    /// stable-shape contract holds even when there are no closures.
    /// </summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsDenseZeroHistogram()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(4);
        lines[1].Should().Be("Approved,0");
        lines[2].Should().Be("Rejected,0");
        lines[3].Should().Be("Cancelled,0");
    }

    /// <summary>
    /// Edge case — a closed dossier whose application is soft-deleted is excluded from the
    /// histogram because the join filters on the application's active flag.
    /// </summary>
    [Fact]
    public async Task Execute_ApplicationSoftDeleted_ExcludesDossierFromHistogram()
    {
        var harness = Harness.Create();
        await harness.SeedClosedDossierAsync(ApplicationStatus.Approved, ClockNow.AddDays(-1), applicationActive: false);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // All three outcomes should be zero — no row was counted.
        lines.Should().HaveCount(4);
        lines[1].Should().Be("Approved,0");
    }

    /// <summary>Missing window parameters must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingParameters_ReturnsValidationFailed()
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

        /// <summary>Cached passport id reused across all seeded dossiers.</summary>
        private long? _passportId;

        /// <summary>Cached solicitant id so seeding does not churn rows.</summary>
        private long? _solicitantId;

        /// <summary>Monotonic counter so dossier numbers do not collide across seeds.</summary>
        private int _dossierCounter;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-dos-clo-out-{Guid.NewGuid():N}")
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
        /// Seeds a closed <see cref="Dossier"/> whose underlying application carries the supplied
        /// status. The dossier's <see cref="Dossier.ClosedAtUtc"/> is the supplied moment;
        /// <see cref="AuditableEntity.CreatedAtUtc"/> is a day earlier for realism.
        /// </summary>
        public async Task SeedClosedDossierAsync(
            ApplicationStatus status,
            DateTime closedAtUtc,
            bool isActive = true,
            bool applicationActive = true)
        {
            var passportId = await EnsurePassportAsync();
            var solicitantId = await EnsureSolicitantAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = closedAtUtc.AddDays(-1),
                SolicitantId = solicitantId,
                ServicePassportId = passportId,
                Status = status,
                FormPayloadJson = "{}",
                SubmittedAtUtc = closedAtUtc.AddDays(-1),
                ClosedAtUtc = closedAtUtc,
                IsActive = applicationActive,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            _dossierCounter++;
            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = closedAtUtc.AddDays(-1),
                ApplicationId = app.Id,
                DossierNumber = $"D-{_dossierCounter:D5}",
                ClosedAtUtc = closedAtUtc,
                IsActive = isActive,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }

        private async Task<long> EnsurePassportAsync()
        {
            if (_passportId is { } id) return id;
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-X",
                NameRo = "Serviciu X",
                DescriptionRo = "Serviciu X",
                FormSchemaJson = "{}",
                WorkflowCode = "WF",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();
            _passportId = passport.Id;
            return passport.Id;
        }

        private async Task<long> EnsureSolicitantAsync()
        {
            if (_solicitantId is { } id) return id;
            const string nationalId = "2000000077777";
            var s = new Solicitant
            {
                CreatedAtUtc = ClockNow,
                NationalId = nationalId,
                NationalIdHash = IdHashHelper.Hash(nationalId),
                Kind = ApplicantKind.NaturalPerson,
                DisplayName = "Test",
                IsActive = true,
            };
            Db.Solicitants.Add(s);
            await Db.SaveChangesAsync();
            _solicitantId = s.Id;
            return s.Id;
        }
    }
}
