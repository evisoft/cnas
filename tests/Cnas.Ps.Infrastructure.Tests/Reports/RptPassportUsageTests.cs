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
/// Integration tests for the Annex 6i report <c>RPT-PASSPORT-USAGE</c> — count of
/// <see cref="ServiceApplication"/> rows per <see cref="ServicePassport.Code"/> in the half-open
/// UTC window <c>[fromUtc, toUtc)</c>. Soft-deleted applications and soft-deleted passports are
/// both excluded; rows are ordered by Count desc then ServiceCode (Ordinal). Service passports
/// with zero in-window traffic are not emitted (the KPI focuses on services that carried load).
/// </summary>
public class RptPassportUsageTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-PASSPORT-USAGE";

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
        firstLine.Should().Be("Service Code,Service Title (RO),Application Count");
    }

    /// <summary>
    /// Seeds three SP-A applications in window, one SP-B in window, one SP-A out of window
    /// (excluded), and one soft-deleted SP-A in window (excluded). Verifies the Count desc
    /// ordering: SP-A leads with 3 then SP-B with 1.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsByPassportCodeAndOrdersByCountDesc()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync("SP-A", "Serviciu A", ClockNow.AddDays(-2));
        await harness.SeedApplicationAsync("SP-A", "Serviciu A", ClockNow.AddDays(-3));
        await harness.SeedApplicationAsync("SP-A", "Serviciu A", ClockNow.AddDays(-4));
        await harness.SeedApplicationAsync("SP-B", "Serviciu B", ClockNow.AddDays(-5));
        // Out-of-window — excluded.
        await harness.SeedApplicationAsync("SP-A", "Serviciu A", ClockNow.AddDays(-100));
        // Soft-deleted in-window — excluded.
        await harness.SeedApplicationAsync("SP-A", "Serviciu A", ClockNow.AddDays(-6), isActive: false);

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 2 passport rows.
        lines.Should().HaveCount(3);
        // Count desc — SP-A first.
        lines[1].Should().Be("SP-A,Serviciu A,3");
        lines[2].Should().Be("SP-B,Serviciu B,1");
    }

    /// <summary>An empty window emits only the header row (no zero-padding for passports).</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsOnlyHeader()
    {
        var harness = Harness.Create();

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
    }

    /// <summary>
    /// Edge case — a passport with no in-window applications is not emitted at all. Seed a
    /// passport then an application on a different passport in the same window; only the
    /// second passport should appear in the output.
    /// </summary>
    [Fact]
    public async Task Execute_PassportWithoutTraffic_IsExcludedFromOutput()
    {
        var harness = Harness.Create();
        // SP-EMPTY exists but has no in-window applications.
        await harness.SeedPassportOnlyAsync("SP-EMPTY", "Serviciu Gol");
        await harness.SeedApplicationAsync("SP-LIVE", "Serviciu Activ", ClockNow.AddDays(-2));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // 1 header + 1 row (SP-LIVE only); SP-EMPTY is excluded.
        lines.Should().HaveCount(2);
        lines[1].Should().Be("SP-LIVE,Serviciu Activ,1");
        text.Should().NotContain("SP-EMPTY");
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

        /// <summary>Caches passport ids by code so the same passport is re-used across applications.</summary>
        private readonly Dictionary<string, long> _passports = new(StringComparer.Ordinal);

        /// <summary>Cached solicitant id so seeding doesn't churn rows.</summary>
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-passusage-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a single <see cref="ServicePassport"/> with no applications attached.</summary>
        public async Task SeedPassportOnlyAsync(string code, string titleRo)
        {
            await EnsurePassportAsync(code, titleRo);
        }

        /// <summary>
        /// Seeds a single <see cref="ServiceApplication"/> whose
        /// <see cref="AuditableEntity.CreatedAtUtc"/> is the supplied moment, attached to the
        /// passport identified by <paramref name="serviceCode"/>. The passport row is created
        /// on first reference and cached for re-use.
        /// </summary>
        public async Task SeedApplicationAsync(string serviceCode, string titleRo, DateTime createdUtc, bool isActive = true)
        {
            var passportId = await EnsurePassportAsync(serviceCode, titleRo);
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

        private async Task<long> EnsurePassportAsync(string code, string titleRo)
        {
            if (_passports.TryGetValue(code, out var id)) return id;
            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = code,
                NameRo = titleRo,
                DescriptionRo = titleRo,
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
            const string nationalId = "2000000088888";
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
