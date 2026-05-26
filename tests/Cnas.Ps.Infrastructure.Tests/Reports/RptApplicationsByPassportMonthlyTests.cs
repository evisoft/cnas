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
/// Integration tests for the Annex 6j report <c>RPT-APPLICATIONS-BY-PASSPORT-MONTHLY</c> — dense
/// (passport × month) count of <see cref="ServiceApplication"/> rows whose
/// <see cref="AuditableEntity.CreatedAtUtc"/> falls in the half-open UTC window
/// <c>[fromUtc, toUtc)</c>. Soft-deleted applications and passports are excluded; every passport
/// with at least one application in the window emits a row for every month, with zero counts
/// where appropriate.
/// </summary>
public class RptApplicationsByPassportMonthlyTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-APPLICATIONS-BY-PASSPORT-MONTHLY";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Service Code,Month (UTC),Application Count");
    }

    /// <summary>
    /// Seeds two SP-A applications in March 2026 and one in May 2026, plus one SP-B in April
    /// 2026. The window spans March-April-May (three months). Verifies the dense (passport ×
    /// month) shape: 6 rows total (2 passports × 3 months), with the zero-count April / May
    /// cells emitted for SP-A and SP-B respectively, and rows ordered by ServiceCode Ordinal
    /// then Month ascending.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_EmitsDensePassportByMonthMatrix()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync("SP-A", new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Utc));
        await harness.SeedApplicationAsync("SP-A", new DateTime(2026, 3, 20, 8, 0, 0, DateTimeKind.Utc));
        await harness.SeedApplicationAsync("SP-A", new DateTime(2026, 5, 10, 8, 0, 0, DateTimeKind.Utc));
        await harness.SeedApplicationAsync("SP-B", new DateTime(2026, 4, 15, 8, 0, 0, DateTimeKind.Utc));

        var paramsJson = BuildParams(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 2 passports × 3 months = 6 dense rows.
        lines.Should().HaveCount(7);
        // SP-A rows first (Ordinal), then SP-B; each block ordered by month ascending.
        lines[1].Should().Be("SP-A,2026-03,2");
        lines[2].Should().Be("SP-A,2026-04,0");
        lines[3].Should().Be("SP-A,2026-05,1");
        lines[4].Should().Be("SP-B,2026-03,0");
        lines[5].Should().Be("SP-B,2026-04,1");
        lines[6].Should().Be("SP-B,2026-05,0");
    }

    /// <summary>An empty window emits only the header row (no passports to expand into months).</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsOnlyHeader()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
    }

    /// <summary>
    /// Edge case — a soft-deleted application in window is excluded. Seed two applications, one
    /// active and one soft-deleted, both for SP-A in the same month. Only the active one
    /// contributes to the count.
    /// </summary>
    [Fact]
    public async Task Execute_SoftDeletedApplication_ExcludedFromCount()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync("SP-A", new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Utc));
        await harness.SeedApplicationAsync("SP-A", new DateTime(2026, 3, 6, 8, 0, 0, DateTimeKind.Utc), isActive: false);

        var paramsJson = BuildParams(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 1 passport × 1 month.
        lines.Should().HaveCount(2);
        lines[1].Should().Be("SP-A,2026-03,1");
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

        /// <summary>Cached passport ids by code so the same passport is re-used across applications.</summary>
        private readonly Dictionary<string, long> _passports = new(StringComparer.Ordinal);

        /// <summary>Cached solicitant id so seeding does not churn rows.</summary>
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-apps-pp-month-{Guid.NewGuid():N}")
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
        /// Seeds a single <see cref="ServiceApplication"/> whose
        /// <see cref="AuditableEntity.CreatedAtUtc"/> is the supplied moment, attached to the
        /// passport identified by <paramref name="serviceCode"/> (created lazily on first use).
        /// </summary>
        public async Task SeedApplicationAsync(string serviceCode, DateTime createdUtc, bool isActive = true)
        {
            var passportId = await EnsurePassportAsync(serviceCode);
            var solicitantId = await EnsureSolicitantAsync();

            Db.Applications.Add(new ServiceApplication
            {
                CreatedAtUtc = createdUtc,
                SolicitantId = solicitantId,
                ServicePassportId = passportId,
                Status = ApplicationStatus.Submitted,
                FormPayloadJson = "{}",
                SubmittedAtUtc = createdUtc,
                IsActive = isActive,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }

        private async Task<long> EnsurePassportAsync(string code)
        {
            if (_passports.TryGetValue(code, out var id)) return id;
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = code,
                NameRo = code,
                DescriptionRo = code,
                FormSchemaJson = "{}",
                WorkflowCode = "WF",
                MaxProcessingDays = 30,
                IsEnabled = true,
                IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();
            _passports[code] = passport.Id;
            return passport.Id;
        }

        private async Task<long> EnsureSolicitantAsync()
        {
            if (_solicitantId is { } id) return id;
            const string nationalId = "2000000066666";
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
