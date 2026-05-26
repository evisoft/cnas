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
/// Integration tests for the Annex 6f report <c>RPT-CANCELLATIONS-BY-REASON</c> — counts
/// of cancelled applications grouped by reason code inside the UTC window <c>[fromUtc, toUtc)</c>.
/// <see cref="ApplicationStatus"/> has no dedicated <c>Cancelled</c> value at the present
/// data-model iteration; the closest semantic is <see cref="ApplicationStatus.Withdrawn"/>
/// (application withdrawn before final decision). This report buckets every Withdrawn
/// application whose <see cref="ServiceApplication.ClosedAtUtc"/> falls in the window into
/// the <c>UNKNOWN</c> reason code per the static lookup pattern that <c>RPT-REJECTION-REASONS</c>
/// uses. Rows are ordered by Count desc, then ReasonCode (Ordinal).
/// </summary>
public class RptCancellationsByReasonTests
{
    /// <summary>Fixed UTC clock — every seed row uses it for AuditableEntity timestamps.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-CANCELLATIONS-BY-REASON";

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
        firstLine.Should().Be("Reason Code,Reason (RO),Count");
    }

    /// <summary>
    /// Two Withdrawn applications closed in window — both land in the <c>UNKNOWN</c> bucket
    /// with the friendly RO label. The closed Approved application is excluded.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_BucketsWithdrawnIntoUnknown()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync(ApplicationStatus.Withdrawn, ClockNow.AddDays(-5));
        await harness.SeedApplicationAsync(ApplicationStatus.Withdrawn, ClockNow.AddDays(-3));
        await harness.SeedApplicationAsync(ApplicationStatus.Approved, ClockNow.AddDays(-2));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("UNKNOWN,Motiv nespecificat,2");
    }

    /// <summary>Applications closed outside the window must not contribute.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesOutOfWindow()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync(ApplicationStatus.Withdrawn, ClockNow.AddDays(-5));
        await harness.SeedApplicationAsync(ApplicationStatus.Withdrawn, ClockNow.AddDays(-200));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("UNKNOWN,Motiv nespecificat,1");
    }

    /// <summary>Non-Withdrawn statuses (Approved, Rejected) are excluded.</summary>
    [Fact]
    public async Task Execute_ExcludesNonCancelledStatuses()
    {
        var harness = Harness.Create();
        await harness.SeedApplicationAsync(ApplicationStatus.Approved, ClockNow.AddDays(-3));
        await harness.SeedApplicationAsync(ApplicationStatus.Rejected, ClockNow.AddDays(-2));

        var paramsJson = BuildParams(ClockNow.AddDays(-30), ClockNow);
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // No body rows — header only.
        lines.Should().HaveCount(1);
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
                .UseInMemoryDatabase($"cnas-rpt-cancellations-{Guid.NewGuid():N}")
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
        /// Seeds a <see cref="ServiceApplication"/> with the supplied <paramref name="status"/>
        /// whose <see cref="ServiceApplication.ClosedAtUtc"/> equals <paramref name="closedUtc"/>.
        /// </summary>
        public async Task SeedApplicationAsync(ApplicationStatus status, DateTime closedUtc)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-CAN",
                    NameRo = "Can", DescriptionRo = "Can",
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000088888",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            Db.Applications.Add(new ServiceApplication
            {
                CreatedAtUtc = closedUtc.AddDays(-10),
                SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value,
                Status = status,
                FormPayloadJson = "{}",
                SubmittedAtUtc = closedUtc.AddDays(-10),
                ClosedAtUtc = closedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
