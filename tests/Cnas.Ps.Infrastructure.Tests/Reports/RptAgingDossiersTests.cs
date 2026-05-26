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
/// Integration tests for the Annex 6 report <c>RPT-AGING-DOSSIERS</c> — distribution of
/// open dossiers (<see cref="Dossier.ClosedAtUtc"/> is null) bucketed by age in days.
/// The five buckets in declared order are: <c>&lt;30 days</c>, <c>30-60</c>, <c>60-90</c>,
/// <c>90-180</c>, <c>&gt;180</c>. All buckets are emitted (zero counts included) so that
/// downstream consumers can render a consistent histogram.
/// </summary>
public class RptAgingDossiersTests
{
    /// <summary>Fixed UTC clock so age-day calculations are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-AGING-DOSSIERS";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedOpenAsync(ClockNow.AddDays(-10));

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Age Bucket,Count");
    }

    /// <summary>
    /// Seeds dossiers across all five buckets and asserts each bucket carries the right
    /// count. Boundary semantics: <c>30-60</c> means [30, 60); <c>90-180</c> means [90, 180);
    /// <c>&gt;180</c> means ≥180.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_BucketsCorrectly()
    {
        var harness = Harness.Create();
        await harness.SeedOpenAsync(ClockNow.AddDays(-10));   // <30
        await harness.SeedOpenAsync(ClockNow.AddDays(-15));   // <30
        await harness.SeedOpenAsync(ClockNow.AddDays(-45));   // 30-60
        await harness.SeedOpenAsync(ClockNow.AddDays(-75));   // 60-90
        await harness.SeedOpenAsync(ClockNow.AddDays(-100));  // 90-180
        await harness.SeedOpenAsync(ClockNow.AddDays(-200));  // >180

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("<30 days,2");
        lines.Should().Contain("30-60,1");
        lines.Should().Contain("60-90,1");
        lines.Should().Contain("90-180,1");
        lines.Should().Contain(">180,1");
    }

    /// <summary>Closed dossiers must NOT appear in any bucket.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesClosed()
    {
        var harness = Harness.Create();
        await harness.SeedOpenAsync(ClockNow.AddDays(-15));        // counted
        await harness.SeedClosedAsync(ClockNow.AddDays(-15), ClockNow.AddDays(-1));  // ignored

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("<30 days,1");
    }

    /// <summary>
    /// Every bucket appears in the output even when its count is zero — downstream charting
    /// must not see a sparse series. We assert this by passing in a single dossier and
    /// verifying every bucket label is present.
    /// </summary>
    [Fact]
    public async Task Execute_EmitsAllBuckets_EvenWhenZero()
    {
        var harness = Harness.Create();
        await harness.SeedOpenAsync(ClockNow.AddDays(-5));  // only <30 has data

        var result = await harness.Service.GenerateAsync(Code, "{}", ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("<30 days,");
        text.Should().Contain("30-60,");
        text.Should().Contain("60-90,");
        text.Should().Contain("90-180,");
        text.Should().Contain(">180,");
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

        /// <summary>Cached passport id so seeding doesn't churn rows.</summary>
        private long? _passportId;

        /// <summary>Cached solicitant id so seeding doesn't churn rows.</summary>
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-aging-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds an open Dossier (<c>ClosedAtUtc</c> null) with the given received date.</summary>
        public Task SeedOpenAsync(DateTime receivedUtc) => SeedAsync(receivedUtc, closedAtUtc: null);

        /// <summary>Seeds a closed Dossier so we can verify exclusion of closed rows.</summary>
        public Task SeedClosedAsync(DateTime receivedUtc, DateTime closedAtUtc) => SeedAsync(receivedUtc, closedAtUtc);

        /// <summary>Shared dossier seed.</summary>
        private async Task SeedAsync(DateTime receivedUtc, DateTime? closedAtUtc)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-AGE",
                    NameRo = "Age", DescriptionRo = "Age",
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000099999",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = receivedUtc,
                SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value,
                Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}",
                SubmittedAtUtc = receivedUtc,
                IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            Db.Dossiers.Add(new Dossier
            {
                CreatedAtUtc = receivedUtc,
                ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                ClosedAtUtc = closedAtUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
