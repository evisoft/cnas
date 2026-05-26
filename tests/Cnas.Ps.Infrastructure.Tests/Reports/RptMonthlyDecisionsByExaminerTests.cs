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
/// Integration tests for the Annex 6e report <c>RPT-MONTHLY-DECISIONS-BY-EXAMINER</c> —
/// per-examiner monthly decision aggregate. Each row carries the examiner username and
/// three counters: <c>ApprovedCount</c>, <c>RejectedCount</c>, <c>TotalAmountApprovedMdl</c>.
/// Source: applications decided in the supplied calendar month, joined to the workflow-task
/// assignee. The <c>monthUtc</c> parameter is anchored to the first-of-month UTC moment.
/// </summary>
public class RptMonthlyDecisionsByExaminerTests
{
    /// <summary>Fixed UTC clock so window anchors are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-MONTHLY-DECISIONS-BY-EXAMINER";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        var dossierId = await harness.SeedDossierAsync(
            ApplicationStatus.Approved, decidedUtc: new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc), amount: 100m);
        await harness.SeedTaskAsync(dossierId, "exam.one",
            createdUtc: new DateTime(2026, 5, 8, 0, 0, 0, DateTimeKind.Utc),
            completedUtc: new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc));

        var paramsJson = $"{{ \"monthUtc\": \"2026-05-01T00:00:00Z\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Examiner Username,Approved Count,Rejected Count,Total Amount Approved (MDL)");
    }

    /// <summary>
    /// Two examiners closing different mixes in the supplied month. exam.one closes one
    /// Approved (200 MDL) and one Rejected; exam.two closes one Approved (350 MDL).
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_AggregatesPerExaminer()
    {
        var harness = Harness.Create();
        var d1 = await harness.SeedDossierAsync(ApplicationStatus.Approved,
            decidedUtc: new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc), amount: 200m);
        var d2 = await harness.SeedDossierAsync(ApplicationStatus.Rejected,
            decidedUtc: new DateTime(2026, 5, 7, 0, 0, 0, DateTimeKind.Utc), amount: null);
        var d3 = await harness.SeedDossierAsync(ApplicationStatus.Approved,
            decidedUtc: new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc), amount: 350m);

        await harness.SeedTaskAsync(d1, "exam.one",
            new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc));
        await harness.SeedTaskAsync(d2, "exam.one",
            new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 7, 0, 0, 0, DateTimeKind.Utc));
        await harness.SeedTaskAsync(d3, "exam.two",
            new DateTime(2026, 5, 7, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 9, 0, 0, 0, DateTimeKind.Utc));

        var paramsJson = $"{{ \"monthUtc\": \"2026-05-01T00:00:00Z\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("exam.one,1,1,200.00");
        lines.Should().Contain("exam.two,1,0,350.00");
    }

    /// <summary>Applications closed outside the month must not contribute.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesOutOfMonth()
    {
        var harness = Harness.Create();
        var dIn = await harness.SeedDossierAsync(ApplicationStatus.Approved,
            decidedUtc: new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc), amount: 100m);
        var dOut = await harness.SeedDossierAsync(ApplicationStatus.Approved,
            decidedUtc: new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc), amount: 999m);

        await harness.SeedTaskAsync(dIn, "exam.in",
            new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc));
        await harness.SeedTaskAsync(dOut, "exam.out",
            new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc));

        var paramsJson = $"{{ \"monthUtc\": \"2026-05-01T00:00:00Z\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("exam.in");
        text.Should().NotContain("exam.out");
    }

    /// <summary>Missing <c>monthUtc</c> must be rejected.</summary>
    [Fact]
    public async Task Execute_MissingMonth_ReturnsValidationFailed()
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

        /// <summary>Cached scaffolding ids so seeding doesn't churn rows.</summary>
        private long? _passportId;
        private long? _solicitantId;

        /// <summary>Cache of user-profile ids per local login.</summary>
        private readonly Dictionary<string, long> _userIds = new(StringComparer.Ordinal);

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-monthdec-{Guid.NewGuid():N}")
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
        /// Seeds a Dossier whose owning <see cref="ServiceApplication"/> carries
        /// <paramref name="finalStatus"/> with <see cref="ServiceApplication.ClosedAtUtc"/>
        /// set to <paramref name="decidedUtc"/>. Returns the dossier id.
        /// </summary>
        public async Task<long> SeedDossierAsync(
            ApplicationStatus finalStatus, DateTime decidedUtc, decimal? amount)
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-DEC",
                    NameRo = "Dec", DescriptionRo = "Dec",
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000055555",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = decidedUtc.AddDays(-7), SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value, Status = finalStatus,
                FormPayloadJson = "{}", SubmittedAtUtc = decidedUtc.AddDays(-7),
                ClosedAtUtc = decidedUtc, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = decidedUtc.AddDays(-7), ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                ComputedAmountMdl = amount,
                ClosedAtUtc = decidedUtc,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            return dossier.Id;
        }

        /// <summary>
        /// Seeds a WorkflowTask attached to <paramref name="dossierId"/> assigned to the user
        /// identified by <paramref name="examinerLogin"/>. Auto-creates the user on first reference.
        /// </summary>
        public async Task SeedTaskAsync(
            long dossierId, string examinerLogin, DateTime createdUtc, DateTime? completedUtc)
        {
            if (!_userIds.TryGetValue(examinerLogin, out var userId))
            {
                var user = new UserProfile
                {
                    CreatedAtUtc = ClockNow,
                    LocalLogin = examinerLogin,
                    DisplayName = examinerLogin,
                    IsActive = true,
                };
                Db.UserProfiles.Add(user);
                await Db.SaveChangesAsync();
                userId = user.Id;
                _userIds[examinerLogin] = userId;
            }

            Db.WorkflowTasks.Add(new WorkflowTask
            {
                CreatedAtUtc = createdUtc,
                DossierId = dossierId,
                Title = "Examine",
                Status = completedUtc is null ? WorkflowTaskStatus.InProgress : WorkflowTaskStatus.Completed,
                AssignedUserId = userId,
                CompletedAtUtc = completedUtc,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
