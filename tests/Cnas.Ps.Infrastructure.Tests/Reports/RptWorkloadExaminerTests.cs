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
/// Integration tests for the Annex 6 report <c>RPT-WORKLOAD-EXAMINER</c> — aggregated
/// counts of open dossiers grouped by the assigned examiner's login. Sourced from open
/// <see cref="WorkflowTask"/> rows (Pending / InProgress) joined to
/// <see cref="UserProfile"/>.<see cref="UserProfile.LocalLogin"/> via
/// <see cref="WorkflowTask.AssignedUserId"/>. Tasks attached to dossiers that are already
/// closed (or to applications that are closed) are excluded.
/// </summary>
public class RptWorkloadExaminerTests
{
    /// <summary>Fixed UTC clock so the asOfUtc anchor is deterministic.</summary>
    private static readonly DateTime ClockNow = new(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>The stable report code under test.</summary>
    private const string Code = "RPT-WORKLOAD-EXAMINER";

    /// <summary>Locks the report's column shape.</summary>
    [Fact]
    public async Task Definition_HasExpectedCodeAndColumns()
    {
        var harness = Harness.Create();
        await harness.SeedTaskAsync(
            examinerLogin: "exam.one",
            taskStatus: WorkflowTaskStatus.InProgress,
            dossierClosed: false);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        var firstLine = text.Split("\r\n")[0];
        firstLine.Should().Be(
            "Examiner Username,Open Dossiers,In Examination,Waiting Docs,Total");
    }

    /// <summary>
    /// Seeds three examiners with various task workloads and asserts each appears with its
    /// total. Group code <c>WAITING-DOCS</c> on a Pending task counts towards the
    /// "Waiting Docs" bucket; an InProgress task counts towards "In Examination"; everything
    /// else counts towards "Open Dossiers". Total = sum of all open tasks.
    /// </summary>
    [Fact]
    public async Task Execute_WithSeededData_GroupsCountsByExaminer()
    {
        var harness = Harness.Create();
        // exam.one: 1 InProgress + 1 Pending (no waiting-docs group) = 2 total.
        await harness.SeedTaskAsync("exam.one", WorkflowTaskStatus.InProgress, dossierClosed: false);
        await harness.SeedTaskAsync("exam.one", WorkflowTaskStatus.Pending, dossierClosed: false);
        // exam.two: 1 Pending WAITING-DOCS = 1 total in the WaitingDocs bucket.
        await harness.SeedTaskAsync("exam.two", WorkflowTaskStatus.Pending, dossierClosed: false,
            groupCode: "WAITING-DOCS");
        // exam.three: completed task — must NOT contribute (closed task is not open work).
        await harness.SeedTaskAsync("exam.three", WorkflowTaskStatus.Completed, dossierClosed: false);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().Contain("exam.one");
        text.Should().Contain("exam.two");
        text.Should().NotContain("exam.three",
            "Examiners whose tasks are all completed have no open work and must not appear.");
    }

    /// <summary>Tasks attached to closed dossiers must NOT contribute to the workload.</summary>
    [Fact]
    public async Task Execute_RespectsFilter_ExcludesClosedDossiers()
    {
        var harness = Harness.Create();
        // exam.alpha: open task on CLOSED dossier — must be excluded.
        await harness.SeedTaskAsync("exam.alpha", WorkflowTaskStatus.InProgress, dossierClosed: true);
        // exam.beta: open task on OPEN dossier — must appear.
        await harness.SeedTaskAsync("exam.beta", WorkflowTaskStatus.InProgress, dossierClosed: false);

        var paramsJson = $"{{ \"asOfUtc\": \"{ClockNow:O}\" }}";
        var result = await harness.Service.GenerateAsync(Code, paramsJson, ExportFormat.Csv);

        result.IsSuccess.Should().BeTrue();
        var text = ReadAllText(result.Value);
        text.Should().NotContain("exam.alpha",
            "Workload from closed dossiers must not surface in the report.");
        text.Should().Contain("exam.beta");
    }

    /// <summary>Missing <c>asOfUtc</c> must be rejected — the report needs an anchor moment.</summary>
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

        /// <summary>Caches user-profile ids per login so repeated calls re-use the same row.</summary>
        private readonly Dictionary<string, long> _userIds = new(StringComparer.Ordinal);

        /// <summary>Creates a fresh, isolated harness for one test.</summary>
        public static Harness Create()
        {
            var opts = new DbContextOptionsBuilder<CnasDbContext>()
                .UseInMemoryDatabase($"cnas-rpt-workload-{Guid.NewGuid():N}")
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
        /// Seeds an open Dossier + a single WorkflowTask assigned to the user with the supplied
        /// <paramref name="examinerLogin"/>. When <paramref name="dossierClosed"/> is true the
        /// dossier's <see cref="Dossier.ClosedAtUtc"/> is set so the report excludes it.
        /// </summary>
        public async Task SeedTaskAsync(
            string examinerLogin,
            WorkflowTaskStatus taskStatus,
            bool dossierClosed,
            string? groupCode = null)
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

            var passport = new ServicePassport
            {
                CreatedAtUtc = ClockNow,
                Code = "SP-WL",
                NameRo = "WL", DescriptionRo = "WL",
                FormSchemaJson = "{}", WorkflowCode = "WF",
                MaxProcessingDays = 30, IsEnabled = true, IsActive = true,
            };
            Db.ServicePassports.Add(passport);
            await Db.SaveChangesAsync();

            var solicitant = new Solicitant
            {
                CreatedAtUtc = ClockNow, NationalId = "2000000000999",
                Kind = ApplicantKind.NaturalPerson, DisplayName = "WL Subject", IsActive = true,
            };
            Db.Solicitants.Add(solicitant);
            await Db.SaveChangesAsync();

            var app = new ServiceApplication
            {
                CreatedAtUtc = ClockNow, SolicitantId = solicitant.Id,
                ServicePassportId = passport.Id, Status = ApplicationStatus.UnderExamination,
                FormPayloadJson = "{}", SubmittedAtUtc = ClockNow, IsActive = true,
            };
            Db.Applications.Add(app);
            await Db.SaveChangesAsync();

            var dossier = new Dossier
            {
                CreatedAtUtc = ClockNow,
                ApplicationId = app.Id,
                DossierNumber = $"D-{Guid.NewGuid():N}"[..16],
                AssignedExaminerId = userId,
                ClosedAtUtc = dossierClosed ? ClockNow.AddDays(-1) : null,
                IsActive = true,
            };
            Db.Dossiers.Add(dossier);
            await Db.SaveChangesAsync();

            Db.WorkflowTasks.Add(new WorkflowTask
            {
                CreatedAtUtc = ClockNow,
                DossierId = dossier.Id,
                Title = "Examine",
                Status = taskStatus,
                AssignedUserId = userId,
                GroupCode = groupCode,
                CompletedAtUtc = taskStatus == WorkflowTaskStatus.Completed ? ClockNow : null,
                IsActive = true,
            });
            await Db.SaveChangesAsync();
            _ = CultureInfo.InvariantCulture;
        }
    }
}
