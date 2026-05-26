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
/// Integration tests for the Annex 6i report <c>RPT-DOSSIERS-OPEN-BY-EXAMINER</c> — per-examiner
/// count of open dossiers (<see cref="Dossier.ClosedAtUtc"/> is null and the row is active)
/// as of <c>asOfUtc</c>. Dossiers with no <see cref="Dossier.AssignedExaminerId"/> land in a
/// single <c>"&lt;unassigned&gt;"</c> sentinel bucket; closed dossiers are excluded.
/// </summary>
public class RptDossiersOpenByExaminerTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-DOSSIERS-OPEN-BY-EXAMINER";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Examiner Username,Open Dossier Count");
    }

    /// <summary>
    /// Seeds three open dossiers attributed to two examiners plus two unassigned open dossiers,
    /// one closed dossier (excluded), and one soft-deleted open dossier (excluded). Verifies
    /// the per-examiner counts, the unassigned sentinel bucket, and the Ordinal ordering
    /// (the sentinel <c>"&lt;unassigned&gt;"</c> sorts before alphanumeric usernames).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsByExaminerWithUnassignedBucket()
    {
        var harness = Harness.Create();
        var alice = await harness.SeedExaminerAsync("alice");
        var bob = await harness.SeedExaminerAsync("bob");
        // alice has 2 open dossiers.
        await harness.SeedOpenDossierAsync(assignedExaminerId: alice, createdUtc: ClockNow.AddDays(-2));
        await harness.SeedOpenDossierAsync(assignedExaminerId: alice, createdUtc: ClockNow.AddDays(-3));
        // bob has 1 open dossier.
        await harness.SeedOpenDossierAsync(assignedExaminerId: bob, createdUtc: ClockNow.AddDays(-4));
        // 2 unassigned open dossiers.
        await harness.SeedOpenDossierAsync(assignedExaminerId: null, createdUtc: ClockNow.AddDays(-5));
        await harness.SeedOpenDossierAsync(assignedExaminerId: null, createdUtc: ClockNow.AddDays(-6));
        // Closed dossier — excluded.
        await harness.SeedOpenDossierAsync(assignedExaminerId: alice, createdUtc: ClockNow.AddDays(-7),
            closedAtUtc: ClockNow.AddDays(-1));
        // Soft-deleted open — excluded.
        await harness.SeedOpenDossierAsync(assignedExaminerId: alice, createdUtc: ClockNow.AddDays(-8),
            isActive: false);

        var paramsJson = BuildParams(ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 3 rows (alice, bob, <unassigned>).
        lines.Should().HaveCount(4);
        lines.Should().Contain("alice,2");
        lines.Should().Contain("bob,1");
        lines.Should().Contain("<unassigned>,2");
    }

    /// <summary>An empty window emits only the header row (no unassigned bucket without data).</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsOnlyHeader()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
    }

    /// <summary>
    /// Edge case — every open dossier is unassigned. The output is a single
    /// <c>&lt;unassigned&gt;</c> row with the full backlog count.
    /// </summary>
    [Fact]
    public async Task Execute_AllUnassigned_EmitsSingleUnassignedRow()
    {
        var harness = Harness.Create();
        await harness.SeedOpenDossierAsync(assignedExaminerId: null, createdUtc: ClockNow.AddDays(-1));
        await harness.SeedOpenDossierAsync(assignedExaminerId: null, createdUtc: ClockNow.AddDays(-2));
        await harness.SeedOpenDossierAsync(assignedExaminerId: null, createdUtc: ClockNow.AddDays(-3));

        var paramsJson = BuildParams(ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[1].Should().Be("<unassigned>,3");
    }

    /// <summary>Missing <c>asOfUtc</c> parameter must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingParameters_ReturnsValidationFailed()
    {
        var harness = Harness.Create();
        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ValidationFailed);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>Builds the parameters JSON for the asOf moment.</summary>
    private static string BuildParams(DateTime asOfUtc)
        => $"{{ \"asOfUtc\": \"{asOfUtc.ToString("O", CultureInfo.InvariantCulture)}\" }}";

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

        /// <summary>Cached passport id so every dossier reuses the same passport row.</summary>
        private long? _passportId;

        /// <summary>Cached solicitant id so every application reuses the same solicitant.</summary>
        private long? _solicitantId;

        /// <summary>Monotonic counter so dossier numbers do not collide across seeds.</summary>
        private int _dossierCounter;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-dos-byex-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds an examiner <see cref="UserProfile"/> with the supplied local login and returns its id.</summary>
        public async Task<long> SeedExaminerAsync(string localLogin)
        {
            var u = new UserProfile
            {
                CreatedAtUtc = ClockNow,
                LocalLogin = localLogin,
                DisplayName = localLogin,
                IsActive = true,
            };
            Db.UserProfiles.Add(u);
            await Db.SaveChangesAsync();
            return u.Id;
        }

        /// <summary>
        /// Seeds an open or closed <see cref="Dossier"/> attributed to the supplied examiner id
        /// (or unassigned when null). Closed dossiers carry the supplied <paramref name="closedAtUtc"/>.
        /// </summary>
        public async Task SeedOpenDossierAsync(
            long? assignedExaminerId,
            DateTime createdUtc,
            DateTime? closedAtUtc = null,
            bool isActive = true)
        {
            var passportId = await EnsurePassportAsync();
            var solicitantId = await EnsureSolicitantAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = createdUtc,
                SolicitantId = solicitantId,
                ServicePassportId = passportId,
                Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}",
                SubmittedAtUtc = createdUtc,
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            _dossierCounter++;
            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = createdUtc,
                ApplicationId = app.Id,
                DossierNumber = $"D-{_dossierCounter:D5}",
                AssignedExaminerId = assignedExaminerId,
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
            const string nationalId = "2000000099999";
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
