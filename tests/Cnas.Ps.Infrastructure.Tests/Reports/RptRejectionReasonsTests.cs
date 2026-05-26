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
/// Integration tests for the Annex 6e report <c>RPT-REJECTION-REASONS</c> — the most-frequent
/// rejection reasons inside the half-open UTC window <c>[fromUtc, toUtc)</c>. Source:
/// applications whose <see cref="ServiceApplication.Status"/> is
/// <see cref="ApplicationStatus.Rejected"/> and whose
/// <see cref="ServiceApplication.ClosedAtUtc"/> falls in the window. Because the current
/// entity model does not carry a <c>RejectionReasonCode</c> field, every rejected application
/// falls into the <c>UNKNOWN</c> bucket; the report still surfaces a Romanian friendly label
/// via a static lookup map and counts the rejections. Rows are sorted by Count descending.
/// </summary>
public class RptRejectionReasonsTests
{
    /// <summary>Fixed UTC clock so the window anchors are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-REJECTION-REASONS";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedRejectionAsync(ClockNow.AddDays(-3));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Reason Code,Reason (RO),Count");
    }

    /// <summary>Rejected applications in window are counted under <c>UNKNOWN</c>.</summary>
    [Fact]
    public async Task Execute_WithSeededData_CountsRejectedApplications()
    {
        var harness = Harness.Create();
        await harness.SeedRejectionAsync(ClockNow.AddDays(-3));
        await harness.SeedRejectionAsync(ClockNow.AddDays(-5));
        await harness.SeedRejectionAsync(ClockNow.AddDays(-7));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // With no RejectionReasonCode field on the entity, every rejection bucket-defaults to UNKNOWN.
        lines.Should().Contain(l => l.StartsWith("UNKNOWN,", StringComparison.Ordinal) && l.EndsWith(",3", StringComparison.Ordinal));
    }

    /// <summary>Applications closed outside the window must not contribute.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesOutOfWindow()
    {
        var harness = Harness.Create();
        await harness.SeedRejectionAsync(ClockNow.AddDays(-3));     // in
        await harness.SeedRejectionAsync(ClockNow.AddDays(-200));   // out

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain(l => l.StartsWith("UNKNOWN,", StringComparison.Ordinal) && l.EndsWith(",1", StringComparison.Ordinal));
    }

    /// <summary>Non-rejected applications must be excluded entirely.</summary>
    [Fact]
    public async Task Execute_ExcludesNonRejectedApplications()
    {
        var harness = Harness.Create();
        await harness.SeedRejectionAsync(ClockNow.AddDays(-3));
        await harness.SeedNonRejectionAsync(ApplicationStatus.Approved, ClockNow.AddDays(-3));
        await harness.SeedNonRejectionAsync(ApplicationStatus.Closed, ClockNow.AddDays(-3));
        await harness.SeedNonRejectionAsync(ApplicationStatus.Withdrawn, ClockNow.AddDays(-3));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // Only the one Rejected row contributes.
        lines.Should().Contain(l => l.StartsWith("UNKNOWN,", StringComparison.Ordinal) && l.EndsWith(",1", StringComparison.Ordinal));
    }

    /// <summary>Missing window parameters must be rejected.</summary>
    [Fact]
    public async Task Execute_MissingWindow_ReturnsValidationFailed()
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

        /// <summary>Cached scaffolding ids so seeding doesn't churn rows.</summary>
        private long? _passportId;
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-reject-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a Rejected application whose <see cref="ServiceApplication.ClosedAtUtc"/> is set.</summary>
        public Task SeedRejectionAsync(DateTime closedAtUtc)
            => SeedAsync(ApplicationStatus.Rejected, closedAtUtc);

        /// <summary>Seeds an application with a non-Rejected status to prove the status filter.</summary>
        public Task SeedNonRejectionAsync(ApplicationStatus status, DateTime closedAtUtc)
            => SeedAsync(status, closedAtUtc);

        private async Task SeedAsync(ApplicationStatus status, DateTime closedAtUtc)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-REJ",
                    NameRo = "Rej", DescriptionRo = "Rej",
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000044444",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            Db.Applications.Add(new ServiceApplication
            {
                CreatedAtUtc = closedAtUtc.AddDays(-5),
                SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value,
                Status = status,
                FormPayloadJson = "{}",
                SubmittedAtUtc = closedAtUtc.AddDays(-5),
                ClosedAtUtc = closedAtUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
