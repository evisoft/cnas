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
/// Integration tests for the Annex 6f report <c>RPT-EXAMINER-AVG-CASELOAD</c> — per-examiner
/// open caseload and average task age (in days) as of a UTC moment. Source:
/// <see cref="WorkflowTask"/> rows whose <see cref="WorkflowTask.CompletedAtUtc"/> is null,
/// grouped by the assignee's <see cref="UserProfile.LocalLogin"/> (or
/// <see cref="UserProfile.DisplayName"/> fallback). Average age uses
/// <c>(asOfUtc − WorkflowTask.CreatedAtUtc).TotalDays</c>.
/// </summary>
public class RptExaminerAvgCaseloadTests
{
    /// <summary>Fixed UTC clock so age computations are deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-EXAMINER-AVG-CASELOAD";

    /// <summary>Locks the report's column shape (aggregated — no sqid fields).</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        var d = await harness.SeedDossierAsync();
        await harness.SeedOpenTaskAsync(d, "exam.one", createdUtc: ClockNow.AddDays(-3));

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be("Examiner Username,Open Cases,Avg Age (Days)");
    }

    /// <summary>
    /// Two open tasks for exam.one (ages 5 and 15 → avg 10.00 days) and one for exam.two
    /// (age 3 → avg 3.00 days). One completed task should not contribute.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_AveragesPerExaminer()
    {
        var harness = Harness.Create();
        var d = await harness.SeedDossierAsync();
        await harness.SeedOpenTaskAsync(d, "exam.one", ClockNow.AddDays(-5));
        await harness.SeedOpenTaskAsync(d, "exam.one", ClockNow.AddDays(-15));
        await harness.SeedOpenTaskAsync(d, "exam.two", ClockNow.AddDays(-3));
        // Completed task — must be excluded.
        await harness.SeedCompletedTaskAsync(d, "exam.one",
            createdUtc: ClockNow.AddDays(-30), completedUtc: ClockNow.AddDays(-1));

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Contain("exam.one,2,10.00");
        lines.Should().Contain("exam.two,1,3.00");
    }

    /// <summary>Completed tasks are excluded — only <c>CompletedAtUtc IS NULL</c> rows count.</summary>
    [Fact]
    public async Task Execute_ExcludesCompletedTasks()
    {
        var harness = Harness.Create();
        var d = await harness.SeedDossierAsync();
        await harness.SeedCompletedTaskAsync(d, "exam.closed",
            createdUtc: ClockNow.AddDays(-7), completedUtc: ClockNow.AddDays(-1));

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().NotContain("exam.closed");
    }

    /// <summary>Tasks without an assignee (group inbox) are excluded.</summary>
    [Fact]
    public async Task Execute_ExcludesUnassignedTasks()
    {
        var harness = Harness.Create();
        var d = await harness.SeedDossierAsync();
        await harness.SeedUnassignedOpenTaskAsync(d, ClockNow.AddDays(-3));

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // No body rows — only the header survives.
        lines.Should().HaveCount(1);
    }

    /// <summary>Missing <c>asOfUtc</c> must be rejected with <see cref="ErrorCodes.ValidationFailed"/>.</summary>
    [Fact]
    public async Task Execute_MissingAsOfUtc_ReturnsValidationFailed()
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

        /// <summary>Caches user-profile ids per login.</summary>
        private readonly Dictionary<string, long> _userIds = new(StringComparer.Ordinal);

        /// <summary>Cached scaffolding ids so seeding doesn't churn rows.</summary>
        private long? _passportId;
        private long? _solicitantId;

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-caseload-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new CnasDbContext(opts);
            var sqids = Substitute.For<ISqidService>();
            sqids.Encode(Arg.Any<long>()).Returns(call => $"sqid-{call.Arg<long>()}");
            var clock = new StubClock(ClockNow);
            var service = new ReportingService(db, clock, sqids, NullLogger<ReportingService>.Instance, IdHashHelper.Instance);
            return new Harness { Db = db, Service = service };
        }

        /// <summary>Seeds a single Dossier and returns its id.</summary>
        public async Task<long> SeedDossierAsync()
        {
            if (_passportId is null)
            {
                var passport = new ServicePassport
                {
                    CreatedAtUtc = ClockNow,
                    Code = "SP-CL",
                    NameRo = "Cl", DescriptionRo = "Cl",
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
                    CreatedAtUtc = ClockNow, NationalId = "2000000077777",
                    Kind = ApplicantKind.NaturalPerson, DisplayName = "Test", IsActive = true,
                };
                Db.Solicitants.Add(s);
                await Db.SaveChangesAsync();
                _solicitantId = s.Id;
            }

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow.AddDays(-30), SolicitantId = _solicitantId.Value,
                ServicePassportId = _passportId.Value, Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}", SubmittedAtUtc = ClockNow.AddDays(-30), IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow.AddDays(-30), ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16], IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();
            return dossier.Id;
        }

        /// <summary>Seeds an open (uncompleted) WorkflowTask assigned to the named examiner.</summary>
        public async Task SeedOpenTaskAsync(long dossierId, string examinerLogin, DateTime createdUtc)
            => await AddTaskAsync(dossierId, examinerLogin, createdUtc, completedUtc: null);

        /// <summary>Seeds a completed WorkflowTask assigned to the named examiner.</summary>
        public async Task SeedCompletedTaskAsync(long dossierId, string examinerLogin, DateTime createdUtc, DateTime completedUtc)
            => await AddTaskAsync(dossierId, examinerLogin, createdUtc, completedUtc);

        /// <summary>Seeds an open WorkflowTask with no assignee (group-inbox style).</summary>
        public async Task SeedUnassignedOpenTaskAsync(long dossierId, DateTime createdUtc)
        {
            Db.WorkflowTasks.Add(new WorkflowTask
            {
                CreatedAtUtc = createdUtc,
                DossierId = dossierId,
                Title = "GroupInbox",
                Status = WorkflowTaskStatus.Pending,
                AssignedUserId = null,
                GroupCode = "group.inbox",
                CompletedAtUtc = null,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        /// <summary>Shared task-insert helper used by both Open and Completed paths.</summary>
        private async Task AddTaskAsync(long dossierId, string examinerLogin, DateTime createdUtc, DateTime? completedUtc)
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
