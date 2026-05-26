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
/// Integration tests for the Annex 6g report <c>RPT-INSURED-PERSONS-NEW</c> — insured
/// persons whose <see cref="AuditableEntity.CreatedAtUtc"/> falls in <c>[fromUtc, toUtc)</c>.
/// Row carries Sqid-encoded id, IDNP, full name, and <see cref="InsuredPerson.RegisteredAtUtc"/>.
/// Soft-deleted rows (<see cref="AuditableEntity.IsActive"/> = false) must be excluded.
/// </summary>
public class RptInsuredPersonsNewTests
{
    /// <summary>Fixed UTC clock so date arithmetic is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-INSURED-PERSONS-NEW";

    /// <summary>Locks the report's column shape (includes a Sqid id column).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Insured Person Sqid,IDNP,Full Name,Registered (UTC)");
    }

    /// <summary>
    /// Seeds two in-window persons (one with patronymic, one without) plus an out-of-window
    /// person and a soft-deleted in-window person — both excluded. Verifies the Sqid
    /// encoding and full-name composition.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_EncodesSqidsAndFiltersByWindow()
    {
        var harness = Harness.Create();
        // In-window — included.
        await harness.SeedInsuredPersonAsync(
            "2000000000001", "Popescu", "Ion", "Vasile",
            createdUtc: ClockNow.AddDays(-5), registeredUtc: ClockNow.AddDays(-5), isActive: true);
        // In-window — patronymic null.
        await harness.SeedInsuredPersonAsync(
            "2000000000002", "Ionescu", "Maria", null,
            createdUtc: ClockNow.AddDays(-10), registeredUtc: ClockNow.AddDays(-10), isActive: true);
        // Out-of-window — excluded.
        await harness.SeedInsuredPersonAsync(
            "2000000000003", "Out", "Side", null,
            createdUtc: ClockNow.AddDays(-100), registeredUtc: ClockNow.AddDays(-100), isActive: true);
        // Soft-deleted — excluded.
        await harness.SeedInsuredPersonAsync(
            "2000000000004", "Soft", "Deleted", null,
            createdUtc: ClockNow.AddDays(-3), registeredUtc: ClockNow.AddDays(-3), isActive: false);

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Two data rows after the header.
        lines.Should().HaveCount(3);
        // Every data row carries a sqid-... prefix in column 1 (CLAUDE.md RULE 3).
        lines[1].Should().StartWith("sqid-");
        lines[2].Should().StartWith("sqid-");
        // Both expected IDNPs appear; the excluded ones do not.
        text.Should().Contain("2000000000001");
        text.Should().Contain("2000000000002");
        text.Should().NotContain("2000000000003");
        text.Should().NotContain("2000000000004");
        // Full-name composition — with-patronymic uses 3-token form, without uses 2-token.
        text.Should().Contain("Popescu Ion Vasile");
        text.Should().Contain("Ionescu Maria");
    }

    /// <summary>An empty window produces only the header row — no data rows.</summary>
    [Fact]
    public async Task Execute_EmptyWindow_EmitsOnlyHeader()
    {
        var harness = Harness.Create();

        var paramsJson = $"{{ \"fromUtc\": \"{ClockNow.AddDays(-30):O}\", \"toUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1);
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
                .UseInMemoryDatabase($"cnas-rpt-ipnew-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds an <see cref="InsuredPerson"/> with the supplied identity + lifecycle fields.</summary>
        public async Task SeedInsuredPersonAsync(
            string idnp, string lastName, string firstName, string? patronymic,
            DateTime createdUtc, DateTime registeredUtc, bool isActive)
        {
            Db.InsuredPersons.Add(new InsuredPerson
            {
                CreatedAtUtc = createdUtc,
                Idnp = idnp,
                LastName = lastName,
                FirstName = firstName,
                Patronymic = patronymic,
                BirthDate = new DateOnly(1990, 1, 1),
                RegisteredAtUtc = registeredUtc,
                IsActive = isActive,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
